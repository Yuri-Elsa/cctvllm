"""Decisions for live clip cutting. No video or model code lives here, so it can be tested on its own."""


class ClipPlanner:
    """Turns one person count per detection into start / stop / rollover decisions.

    start     a person has been seen in `confirm` detections in a row: begin a clip (the caller adds the pre-roll)
    stop      nobody has been seen for `postroll` seconds: end the clip
    rollover  the clip reached `max_clip` seconds while a person is still in view: end it and begin the next one,
              so the first clip can be described while the person is still there
    """

    def __init__(self, confirm=2, postroll=4.0, max_clip=20.0):
        self.confirm = max(1, int(confirm))
        self.postroll = float(postroll)
        self.max_clip = float(max_clip)
        self.recording = False
        self.hits = 0
        self.started = 0.0
        self.last_seen = 0.0

    def observe(self, now, people):
        """Call once per detection with the time (seconds) and the number of people seen. Returns a list of actions."""
        if not self.recording:
            self.hits = self.hits + 1 if people > 0 else 0
            if self.hits >= self.confirm:
                self.recording = True
                self.started = self.last_seen = now
                self.hits = 0
                return ["start"]
            return []

        if people > 0:
            self.last_seen = now
        if now - self.last_seen >= self.postroll:
            self.recording = False
            self.hits = 0
            return ["stop"]
        if people > 0 and now - self.started >= self.max_clip:
            self.started = now
            return ["rollover"]
        return []

    def reset(self):
        """The stream went away. Returns True when a clip was in progress and has to be closed."""
        was = self.recording
        self.recording = False
        self.hits = 0
        return was
