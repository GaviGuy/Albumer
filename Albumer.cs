using System;
using ChartHelper.Parsing;
using libZPlay;

namespace Albumer {
    class Albumer {
        //reads audio length from the audio file's metadata
        private static float GetAudioLengthFromSRTB(SRTB input) {
            ZPlay player = new ZPlay();
            String audioPath = FileHelper.CustomPath + "\\AudioClips\\" + input.GetClipInfo(0).ClipAssetReference.AssetName;
            String extension;
            if (player.OpenFile(audioPath + ".ogg", TStreamFormat.sfAutodetect) == true) {
                extension = ".ogg";
            }
            else if (player.OpenFile(audioPath + ".mp3", TStreamFormat.sfAutodetect) == true) {
                extension = ".mp3";
            }
            else {
                Console.WriteLine(player.GetError());
                Console.WriteLine("error reading file: " + audioPath);
                return -1;
            }
            TStreamInfo info = new TStreamInfo();
            player.GetStreamInfo(ref info);
            float ret = (float)(info.Length.sec + (info.Length.ms % 1000) / 1000.0);
            //Console.WriteLine(ret);
            //Console.WriteLine(audioPath + extension);
            return ret;
        }

        //prompts the user for all necessary input and output files
        private static void GetInputParameters(out int numCharts, out String[] sourcePaths, out String nameOut) {
            string input;
            do {
                Console.WriteLine("\nHow many charts to combine:");
                input = Console.ReadLine();
            } while (!Int32.TryParse(input, out numCharts) || numCharts < 2);

            sourcePaths = new string[numCharts];
            for (int i = 0; i < numCharts; i++) {
                do {
                    Console.WriteLine("\nFilename of chart " + (i + 1) + ":");
                    input = Console.ReadLine();
                } while (!FileHelper.TryGetSrtbWithFileName(input, out sourcePaths[i]));
            }

            char answer = 'y';
            do {
                Console.WriteLine("\nOutput title:");
                input = Console.ReadLine();
                if (FileHelper.IsSrtb(input)) {
                    Console.Write("Chart exists with that name. Overwrite? (y/n)\n");
                    answer = (char) Console.Read();
                }
                nameOut = input;
            } while (Char.ToLower(answer) != 'y');
        }

        //returns the number of complete measures, with optional output for the time and beat of the last measure
        private static int TraverseChart(SRTB srtb, float endTime, out int lastMeasureStartBeat, out float lastMeasureStartTime) {
            var bpms = srtb.GetClipInfo(0).BpmMarkers;
            var timeSigs = srtb.GetClipInfo(0).TimeSignatureMarkers;
            lastMeasureStartBeat = 0;
            lastMeasureStartTime = 0;

            float curTime = srtb.GetClipInfo(0).BpmMarkers[0].ClipTime;
            float curBpm = bpms[0].BeatLength;
            int curBeat = 0,
                sigIndex = 0,
                bpmIndex = 0,
                measure = 0,
                beatsLeft = timeSigs[0].TicksPerBar,
                denom = timeSigs[0].TickDivisor;

            while ((curTime + curBpm * (float)(denom / 4.0)) < endTime) {
                curBeat++;
                beatsLeft--;

                //todo: this statement doesn't account for interpolate markers
                curTime += curBpm * (float)(denom / 4.0);

                //if a new measure
                if (beatsLeft <= 0) {
                    measure++;
                    //if a new timeSig marker
                    if (sigIndex < timeSigs.Count - 1 && timeSigs[sigIndex + 1].StartingBeat <= curBeat) {
                        sigIndex++;
                        denom = timeSigs[sigIndex].TickDivisor;
                    }
                    beatsLeft = timeSigs[sigIndex].TicksPerBar;
                    lastMeasureStartBeat = curBeat;
                    lastMeasureStartTime = curTime;
                }

                //if a new bpm marker
                if (bpmIndex < bpms.Count - 1 && (bpms[bpmIndex + 1].ClipTime - 0.005f) <= curTime) {
                    bpmIndex++;
                    curBpm = bpms[bpmIndex].BeatLength;
                    curTime = bpms[bpmIndex].ClipTime; //jank way to account for 1-beat speedup interpolation
                }
            }
            return measure;
        }


        static void Main(string[] args) {

            //todo: use an options library to implement flags for overwriting, overwriting midpoints, autopopulating getInputParameters, etc

            GetInputParameters(out int numCharts, out string[] sourcePaths, out string nameOut);

            SRTB[] srtbs = new SRTB[numCharts];
            for (int i = 0; i < numCharts; i++) {
                srtbs[i] = SRTB.DeserializeFromFile(sourcePaths[i]);
            }

            float[] audioLengths = new float[numCharts];
            float firstChartAudioLength = 0;
            int[] numBeats = new int[numCharts];

            //mod 1: modify all charts to start/stop at audio start/stop
            for (int i = 0; i < numCharts; i++) {
                SRTB.ClipInfo curClipInfo = srtbs[i].GetClipInfo(0);
                SRTB.TrackData[] curNoteData = { srtbs[i].GetTrackData(0), srtbs[i].GetTrackData(1), srtbs[i].GetTrackData(2), srtbs[i].GetTrackData(3), srtbs[i].GetTrackData(4) };
                float audioLength = GetAudioLengthFromSRTB(srtbs[i]);
                audioLengths[i] = audioLength;

                //if first chart, account for start instead of changing it
                if (i == 0) {
                    firstChartAudioLength = audioLengths[i];
                    audioLengths[i] -= curClipInfo.BpmMarkers[0].ClipTime;
                    if (curClipInfo.TimeSignatureMarkers[0].StartingBeat != 0) {
                        //todo: if multiple bpm markers before this time sig marker, fuck yourself
                        //todo: if interpolate bpm marker before this time sig marker, fuck yourself
                        audioLengths[i] -= curClipInfo.TimeSignatureMarkers[0].StartingBeat * curClipInfo.BpmMarkers[0].BeatLength;
                    }
                }

                //otherwise, change the start to happen right at the audio start (t=0)
                else {
                    float noteChange = 0,
                          tOffset = curClipInfo.BpmMarkers[0].ClipTime;

                    //account for negative clipData startBar
                    for (int j = 4; j >= 1; j--) {
                        if (srtbs[i].GetTrackInfo().Difficulties[j].Active) {
                            if (curNoteData[j].ClipData[0].StartBar < 0) {
                                noteChange += curNoteData[j].ClipData[0].StartBar * curClipInfo.BpmMarkers[0].BeatLength * curClipInfo.TimeSignatureMarkers[0].TicksPerBar;
                                break;
                            }
                        }
                    }

                    //modify start
                    //if first time sig isn't on first bpm marker, move it (and all others)
                    if (curClipInfo.TimeSignatureMarkers[0].StartingBeat != 0) {
                        noteChange = curClipInfo.BpmMarkers[0].BeatLength * curClipInfo.TimeSignatureMarkers[0].StartingBeat;
                        curClipInfo.TimeSignatureMarkers[0].StartingBeat = 0;
                        //todo: account for denom

                        //todo: if multiple bpm markers before this time sig marker, fuck yourself
                        //todo: if interpolate bpm marker before this time sig marker, fuck yourself
                    }

                    //if first bpm marker isn't at t=0, move it (and also everything else)
                    if (curClipInfo.BpmMarkers[0].ClipTime != 0) {
                        int bOffset = 0;
                        noteChange += tOffset;

                        //if offset is positive
                        if (curClipInfo.BpmMarkers[0].ClipTime > 0) {
                            float newBeatLength = curClipInfo.BpmMarkers[0].ClipTime;
                            Boolean skipSig = false;
                            //too short to interpolate
                            if (newBeatLength < 0.003) {
                                newBeatLength += curClipInfo.BpmMarkers[0].BeatLength;
                                curClipInfo.BpmMarkers[0].ClipTime += curClipInfo.BpmMarkers[0].BeatLength;
                                skipSig = true;
                            }
                            //too short for a normal marker
                            if (newBeatLength < 0.125) {
                                curClipInfo.BpmMarkers.Insert(0, new SRTB.BPMMarker { ClipTime = 0, BeatLength = curClipInfo.BpmMarkers[0].BeatLength, Type = SRTB.BPMMarkerType.Interpolated });
                                bOffset = 1;
                            }
                            //find how many beats can fit at base bpm
                            else {
                                bOffset += (int)(newBeatLength / curClipInfo.BpmMarkers[0].BeatLength);
                                if (newBeatLength > 0.25 && !skipSig) bOffset++; //looks cleaner, but only works below 250bpm
                                newBeatLength /= bOffset;
                                curClipInfo.BpmMarkers.Insert(0, new SRTB.BPMMarker { ClipTime = 0, BeatLength = newBeatLength, Type = 0 });
                            }

                            //set time sigs
                            if (!skipSig) {
                                curClipInfo.TimeSignatureMarkers.Insert(1, new SRTB.TimeSignatureMarker {
                                    BeatLengthDotted = 0,
                                    BeatLengthType = 0,
                                    StartingBeat = bOffset,
                                    TicksPerBar = curClipInfo.TimeSignatureMarkers[0].TicksPerBar,
                                    TickDivisor = curClipInfo.TimeSignatureMarkers[0].TickDivisor
                                });
                                curClipInfo.TimeSignatureMarkers[0] = new SRTB.TimeSignatureMarker {
                                    BeatLengthDotted = 0,
                                    BeatLengthType = 0,
                                    StartingBeat = 0,
                                    TicksPerBar = bOffset,
                                    TickDivisor = curClipInfo.TimeSignatureMarkers[0].TickDivisor
                                };
                            }


                        }

                        //if offset is negative
                        else if (curClipInfo.BpmMarkers[0].ClipTime < 0) {
                            float tempT = curClipInfo.BpmMarkers[0].ClipTime;
                            int tempB = 0;
                            while (tempT < 0.003) { //traverse until past t=0 (.003 for interpolation limits)
                                //todo: account for bpm changes/interpolation
                                //todo: breaks with non x/4 markers?
                                tempT += curClipInfo.BpmMarkers[0].BeatLength;
                                tempB++;
                            }
                            //todo: account for bpm changes/interpolation
                            curClipInfo.BpmMarkers.Insert(1, new SRTB.BPMMarker { ClipTime = tempT, BeatLength = curClipInfo.BpmMarkers[0].BeatLength, Type = 0 });
                            curClipInfo.BpmMarkers[0].ClipTime = 0;
                            curClipInfo.BpmMarkers[0].Type = SRTB.BPMMarkerType.Interpolated;

                            //set time sigs
                            //todo: divisors other than 4
                            if (tempB != 1) {
                                curClipInfo.TimeSignatureMarkers.Insert(1, new SRTB.TimeSignatureMarker {
                                    BeatLengthDotted = 0,
                                    BeatLengthType = 0,
                                    StartingBeat = curClipInfo.TimeSignatureMarkers[0].TicksPerBar - ((tempB - 1) % curClipInfo.TimeSignatureMarkers[0].TicksPerBar),
                                    TicksPerBar = curClipInfo.TimeSignatureMarkers[0].TicksPerBar,
                                    TickDivisor = curClipInfo.TimeSignatureMarkers[0].TickDivisor
                                });
                                curClipInfo.TimeSignatureMarkers[0] = new SRTB.TimeSignatureMarker {
                                    BeatLengthDotted = 0,
                                    BeatLengthType = 0,
                                    StartingBeat = 0,
                                    TicksPerBar = (tempB - 1) % curClipInfo.TimeSignatureMarkers[0].TicksPerBar,
                                    TickDivisor = curClipInfo.TimeSignatureMarkers[0].TickDivisor
                                };

                                bOffset = -tempB + 1;
                            }
                        }
                        
                        //if offset is too close to 0
                        else curClipInfo.BpmMarkers[0].ClipTime = 0;

                        //move time sigs (again)
                        for (int j = 2; j < curClipInfo.TimeSignatureMarkers.Count; j++) {
                            curClipInfo.TimeSignatureMarkers[j].StartingBeat += bOffset;
                        }

                    }
                    //move notes
                    for (int j = 0; j < curNoteData.Length; j++) {
                        for (int k = 0; k < curNoteData[j].Notes.Count; k++) {
                            curNoteData[j].Notes[k].Time += noteChange;
                        }
                    }
                }

                //if last chart, leave the ending

                //otherwise, change the end to happen at audio end
                if (i < numCharts - 1) {

                    srtbs[i].SetClipInfo(0, curClipInfo);
                    int measure = TraverseChart(srtbs[i], audioLength, out int lastMeasureBeat, out float lastMeasureTime);

                    for (int j = curClipInfo.BpmMarkers.Count - 1; j > 0; j--) { //delete any bpm markers after the start of the last measure
                        if (curClipInfo.BpmMarkers[j].ClipTime >= lastMeasureTime - 0.005) {
                            curClipInfo.BpmMarkers.RemoveAt(j);
                        }
                    }

                    //how many beats? how much time?
                    Boolean placeTimeSig = true;
                    float remainingTime = audioLength - lastMeasureTime;
                    if (remainingTime < 0.13) { //if this length would exceed 500bpm, borrow a beat, place no time sig
                        lastMeasureTime -= curClipInfo.BpmMarkers[curClipInfo.BpmMarkers.Count - 1].BeatLength;
                        remainingTime = audioLength - lastMeasureTime;
                        placeTimeSig = false;
                    }

                    int remainingBeats = (int) (remainingTime / curClipInfo.BpmMarkers[curClipInfo.BpmMarkers.Count - 1].BeatLength);
                    if (remainingBeats < 1) remainingBeats = 1; //it's possible the remaining time is shorter than 1 beat

                    float newBpm = remainingTime / remainingBeats;

                    numBeats[i] = lastMeasureBeat + remainingBeats;

                    //place a time sig near the end
                    if(placeTimeSig)
                        curClipInfo.TimeSignatureMarkers.Add(new SRTB.TimeSignatureMarker {
                            TickDivisor = 4,
                            TicksPerBar = remainingBeats,
                            BeatLengthDotted = 0,
                            BeatLengthType = 0,
                            StartingBeat = lastMeasureBeat
                        });

                    //place a bpm marker near the end
                    curClipInfo.BpmMarkers.Insert(curClipInfo.BpmMarkers.Count, new SRTB.BPMMarker {
                        ClipTime = lastMeasureTime,
                        BeatLength = newBpm,
                        Type = 0
                    });

                    //set the clipData
                    for (int j = 0; j < 5; j++) {
                        if (curNoteData[j].ClipData.Count < 1)
                            continue;
                        int start = curNoteData[j].ClipData[0].StartBar;
                        curNoteData[j].ClipData.Clear();
                        curNoteData[j].ClipData.Add(new SRTB.ClipData {
                            ClipIndex = 0,
                            StartBar = start,
                            EndBar = start + measure,
                            TransitionIn = SRTB.ClipTransition.FadeOutsideBorder,
                            TransitionInOffset = 0,
                            TransitionInValue = 0.001f,
                            TransitionOut = 0,
                            TransitionOutOffset = 0,
                            TransitionOutValue = 0
                        });
                    }
                }

                //redo cuepoints
                curClipInfo.CuePoints.Clear();
                curClipInfo.CuePoints.Add(new SRTB.CuePoint { Name = srtbs[i].GetTrackInfo().Title, Time = 0 });

                //change title
                var tempTrackInfo = srtbs[i].GetTrackInfo();
                tempTrackInfo.Title += "-MIDPOINT";
                srtbs[i].SetTrackInfo(tempTrackInfo);

                //write in remaining modified data
                srtbs[i].SetClipInfo(0, curClipInfo);
                for (int j = 0; j < 5; j++) {
                    srtbs[i].SetTrackData(j, curNoteData[j]);
                }

                //save to new charts
                sourcePaths[i] = sourcePaths[i].Insert(sourcePaths[i].Length - 5, "-MIDPOINT");
                srtbs[i].SerializeToFile(sourcePaths[i]);
            }

            //STEP 2: Make modifications

            Console.Write("\n\nPlease open and save each chart in Spin Rhythm, fixing any mistakes as you find them.\n\n");

            Console.Write("Select a mode:\n1. one long audioClip for the entire chart (higher quality, must create new audio externally)\n2. an audioClip for each chart (easier)\n\n");
            string input;
            int mode;
            do {
                input = Console.ReadLine();
            } while (Int32.TryParse(input, out mode) && (mode != 1 && mode != 2));

            float concurrentNoteOffset = audioLengths[0];
            float concurrentClipOffset = firstChartAudioLength;
            SRTB.TrackInfo destTrack = srtbs[0].GetTrackInfo();
            SRTB.TrackData[] destData = { srtbs[0].GetTrackData(0), srtbs[0].GetTrackData(1), srtbs[0].GetTrackData(2), srtbs[0].GetTrackData(3), srtbs[0].GetTrackData(4) };
            SRTB.ClipInfo destClip = srtbs[0].GetClipInfo(0);

            //long audio
            if (mode == 1) {

                srtbs[0] = SRTB.DeserializeFromFile(sourcePaths[0]);
                TraverseChart(srtbs[0], firstChartAudioLength + 0.01f, out int beatOffset, out float lastMeasureStartTime);

                //append all charts to the first
                for (int i = 1; i < numCharts; i++) {
                    srtbs[i] = SRTB.DeserializeFromFile(sourcePaths[i]);

                    SRTB.TrackData[] curData = { srtbs[i].GetTrackData(0), srtbs[i].GetTrackData(1), srtbs[i].GetTrackData(2), srtbs[i].GetTrackData(3), srtbs[i].GetTrackData(4) };
                    SRTB.ClipInfo curClip = srtbs[i].GetClipInfo(0);

                    //append to trackData
                    for (int j = 0; j < 5; j++) {
                        for (int k = 0; k < curData[j].Notes.Count; k++) {
                            //notes
                            curData[j].Notes[k].Time += concurrentNoteOffset;
                            destData[j].Notes.Add(curData[j].Notes[k]);
                        }
                        for (int k = 0; k < curData[j].ClipData.Count; k++) {
                            //clip
                            if(destData[j].ClipData.Count > 0)
                                destData[j].ClipData[0].EndBar += curData[j].ClipData[k].EndBar;
                        }
                    }

                    //append to clipInfo
                    for (int j = 0; j < curClip.BpmMarkers.Count; j++) {
                        curClip.BpmMarkers[j].ClipTime += concurrentClipOffset;
                        destClip.BpmMarkers.Add(curClip.BpmMarkers[j]);
                    }
                    for (int j = 0; j < curClip.TimeSignatureMarkers.Count; j++) {
                        curClip.TimeSignatureMarkers[j].StartingBeat += beatOffset;
                        destClip.TimeSignatureMarkers.Add(curClip.TimeSignatureMarkers[j]);
                    }
                    for (int j = 0; j < curClip.CuePoints.Count; j++) {
                        curClip.CuePoints[j].Time += concurrentClipOffset;
                        destClip.CuePoints.Add(curClip.CuePoints[j]);
                    }


                    concurrentNoteOffset += audioLengths[i];
                    concurrentClipOffset += audioLengths[i];
                    TraverseChart(srtbs[i], audioLengths[i] + 0.01f, out int curBeat, out lastMeasureStartTime);
                    beatOffset += curBeat;
                }

                //add dummy clipData so it doesn't remove in game
                for (int i = 0; i < 5; i++) {
                    destData[i].ClipData.Add(new SRTB.ClipData { ClipIndex = 0, StartBar = -2, EndBar = -2, TransitionIn = 0, TransitionInOffset = 0, TransitionInValue = 0, TransitionOut = 0,  TransitionOutOffset = 0, TransitionOutValue = 0});
                }
            }

            //short audios, we need to check that each song ends at the right measure
            else if (mode == 2) {
                char[] answers = new char[numCharts];
                for (int i = 0; i < numCharts; i++) {
                    do {
                        Console.Write("" + i + " - Does " + srtbs[i].GetTrackInfo().Title + " last a measure longer than it should? (y/n)\n");
                        input = Console.ReadLine();
                    } while (!Char.TryParse(input, out answers[i]) || (Char.ToLower(answers[i]) != 'y' && Char.ToLower(answers[i]) != 'n'));
                }

                srtbs[0] = SRTB.DeserializeFromFile(sourcePaths[0]);

                //append all charts to the first
                for (int i = 1; i < numCharts; i++) {
                    srtbs[i] = SRTB.DeserializeFromFile(sourcePaths[i]);

                    SRTB.TrackData[] curData = { srtbs[i].GetTrackData(0), srtbs[i].GetTrackData(1), srtbs[i].GetTrackData(2), srtbs[i].GetTrackData(3), srtbs[i].GetTrackData(4) };

                    //apply any necessary fixes
                    if (Char.ToLower(answers[i]) == 'y') {
                        for (int j = 0; j < 4; j++) {
                            if (curData[j].ClipData.Count > 0)
                                curData[j].ClipData[0].EndBar--;
                        }
                    }

                    //append to trackData
                    for (int j = 0; j < 5; j++) {
                        for (int k = 0; k < curData[j].Notes.Count; k++) {
                            //notes
                            curData[j].Notes[k].Time += concurrentNoteOffset;
                            destData[j].Notes.Add(curData[j].Notes[k]);
                        }
                        for (int k = 0; k < curData[j].ClipData.Count; k++) {
                            //clip
                            curData[j].ClipData[k].ClipIndex = i;
                            curData[j].ClipData[k].TransitionIn = SRTB.ClipTransition.FadeInsideBorder;
                            curData[j].ClipData[k].TransitionInValue = 0.001f;
                            destData[j].ClipData.Add(curData[j].ClipData[k]);

                            destData[j].ClipInfoAssetReferences.Add(new SRTB.AssetReference { Bundle = "CUSTOM", AssetName = "ClipInfo_" + i, Guid = "" });
                        }
                    }

                    //add new clipInfo and a reference to it
                    var newClipString = srtbs[i].LargeStringValuesContainer.Values[6];
                    newClipString.Key = "SO_ClipInfo_ClipInfo_" + i;
                    srtbs[0].LargeStringValuesContainer.Values.Add(newClipString);

                    var newClipUnity = new SRTB.UnityObjectValue { Key = "ClipInfo_" + i, JsonKey = "SO_ClipInfo_ClipInfo_" + i, FullType = "ClipInfo" };
                    srtbs[0].UnityObjectValuesContainer.Values.Add(newClipUnity);

                    srtbs[0].ClipInfoCount++;
                    concurrentNoteOffset += audioLengths[i];

                }

            }

            //modify track info
            destTrack.Title = nameOut;
            destTrack.Subtitle = "Albumer Output";
            destTrack.ArtistName = "";
            destTrack.FeatArtists = "";
            destTrack.Description = "Created using Gavi Guy's chart splicing tool.";

            //paste info back into destination
            for (int i = 0; i < 5; i++) {
                srtbs[0].SetTrackData(i, destData[i]);
            }
            srtbs[0].SetTrackInfo(destTrack);
            srtbs[0].SetClipInfo(0, destClip);

            //serialize
            srtbs[0].SerializeToFile(FileHelper.CustomPath + '\\' + nameOut + ".srtb");


            //mod 2: append all charts after the first
            //notes must have time added, then appended
            //clipInfo must be added in the next index
            //clipInfoCount must be iterated

            //mod 3: request the first beat of each chart, in order, use it to shift the tempomap for setup with the full album audio

            //mod 4: finalize
            //change title
            //change filename
            //write
        }
    }
}