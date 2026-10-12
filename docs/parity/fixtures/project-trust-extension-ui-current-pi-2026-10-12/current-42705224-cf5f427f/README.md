# Current-Pi project-trust extension UI replay

Pins: Pi `4270522473e677ddba7b093caa0064a3df51d459`; PiSharp `cf5f427f4990d7404f80f8a378a64d6731a7a5fd`.

The 12-frame paired PTY flow and same-Pi calibration both match terminal state, renders, and HTTP traces exactly. Both sides run one provider request with the trusted project skill, the same normalized system-prompt section order and hash, and no scenario errors. The three named startup trust-dialog frames also match. The working indicator never shows completed-run usage early; its number of animation frames differs between products and is scheduling-dependent. Normalized provider requests match; the only raw request path difference is the generated system-message content before documentation-package paths are canonicalized. Raw PTY byte streams differ because their terminal control protocols differ.

See [paired evidence](paired/), including the semantic assertion output, and [same-Pi calibration](calibration/).
