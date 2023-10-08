# Albumer
 
Will stitch any given number of Spin Rhythm XD charts together in a sequence.
Designed for use with individual song audio clips, or one long combined audio clip.

To use:
1. Run the program and answer the first few prompts in the console
2. Inspect the generated midpoint files for inaccuracies. Save each one in-game, even if you made no changes
3. Return to the console and hit enter to finalize everything


## Limitations

Not designed for use with charts that:

* Use interpolated bpm markers between two different bpms

* Use interpolated bpm markers to reduce the effective bpm

* Use outlandish time signatures

* Use multiple ClipInfos

* Use non-default clipData in any difficulty

* Start with an interpolated bpm marker

* Have multiple bpm markers before the first time signature marker

* Have multiple time signature markers before the first bpm marker

Currently doesn't copy flight path data

Currently doesn't support RemiXD difficulty