# Changelog

All notable changes to Splicia are listed here.

## [1.0.7] - 2026-10-29

### What's new

- **Trim is back.** Press T (or the trim button) to put a yellow selection over the selected clip, or over the whole timeline when none is selected; double-clicking a clip starts with that clip. Drag the yellow handles to set where it starts and ends, or the grip on the ruler to slide it along; I and O (or [ and ]) set the start and end at the playhead, and Space plays just the selection. It can span several clips and sounds: **Apply trim** (or Enter) keeps only what's inside it, cut where the selection cuts them and moved to the start, and you carry on editing. Cancel (or Esc) leaves the timeline as it was. Exporting while trimming exports just the selection.

### Fixes

- The keyboard shortcuts window no longer cuts off its longer descriptions.

## [1.0.6] - 2026-10-27

### What's new

- **A timeline of clips.** Press S (or the scissors button) to split the clip at the playhead. Click a clip to select it; Delete removes it and leaves a gap, Shift+Delete closes the gap. Drag clips to move them anywhere, or onto other clips to rearrange them. Gaps show black and silent. Undo and redo with the arrow buttons, or Ctrl+Z and Ctrl+Y. This replaces trimming: split twice and delete the ends.
- **Transitions.** Click the dot where two clips meet to choose a straight cut, a crossfade or a fade out and back in, from 0.25 to 2 seconds.
- **Fades.** Fade any clip in or out by dragging the handle at its top corner, or from its right-click menu; the menu under ⋯ fades the start and end of the video.
- **An audio track.** Drop WAV or MP3 files on it (or use the note button) to add music or sound. Move them along the track, drag their ends to trim them, and fade them in and out. The video's own sound shows as a waveform under its clips.
- **Seamless playback.** The app plays the timeline exactly as it will be exported: no pause at cuts, crossfades and fades shown as they happen, and the sound mixed across every join without clicks. Looping is seamless too.
- **A docked control panel.** The panel is now pinned under the video, full width, so it never covers the picture. Unpin it (the pin button or P) to have it float over the video and hide when idle, as before.

### Fixes

- Exporting 4K HDR video to H.264 uses about half the memory.
- Re-encoded exports of 29.97 fps video could gain an extra frame at the end of a cut.

## [1.0.5] - 2026-10-26

### What's new

- **Crop videos.** Press C (or the crop button) to crop with a common aspect ratio (1:1, 16:9, 9:16, 4:3, 3:4, 4:5, 21:9), the video's original ratio, or freely. Drag the corners, edges or the whole frame, then press Enter to keep the crop or Esc to undo it.

### Fixes

- Several bug fixes.

[1.0.7]: https://github.com/fender90/Splicia/releases/tag/v1.0.7
[1.0.6]: https://github.com/fender90/Splicia/releases/tag/v1.0.6
[1.0.5]: https://github.com/fender90/Splicia/releases/tag/v1.0.5
