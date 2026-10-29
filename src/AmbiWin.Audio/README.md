# AmbiWin.Audio

WASAPI loopback capture of the default output device, feeding mono samples to `AmbiWin.Core.Audio.BeatDetector`.
Restarts on default-device changes and reports silence when capture delivers no packets.

Planned for week 2. See the technical design for the pipeline and parameters.
