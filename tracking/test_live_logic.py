import unittest

from live_logic import ClipPlanner


def run(planner, samples):
    """samples: (time, people) pairs. Returns [(time, action), ...]."""
    out = []
    for t, people in samples:
        for action in planner.observe(t, people):
            out.append((t, action))
    return out


class ClipPlannerTests(unittest.TestCase):
    def test_nothing_happens_while_the_scene_is_empty(self):
        self.assertEqual(run(ClipPlanner(), [(i * 0.5, 0) for i in range(40)]), [])

    def test_a_single_detection_is_not_enough(self):
        # confirm=2: one stray detection (a shadow, a reflection) must not start a clip
        self.assertEqual(run(ClipPlanner(confirm=2), [(0, 1), (0.5, 0), (1.0, 1), (1.5, 0)]), [])

    def test_two_detections_in_a_row_start_a_clip(self):
        self.assertEqual(run(ClipPlanner(confirm=2), [(0, 1), (0.5, 1)]), [(0.5, "start")])

    def test_the_clip_ends_after_the_postroll_without_anyone(self):
        planner = ClipPlanner(confirm=2, postroll=4.0)
        events = run(planner, [(0, 1), (0.5, 1), (1.0, 1), (1.5, 0), (3.0, 0), (5.0, 0), (5.5, 0), (6.0, 0)])
        # last seen at 1.0, so the clip stops at the first detection 4 s later (5.0)
        self.assertEqual(events, [(0.5, "start"), (5.0, "stop")])

    def test_a_person_who_comes_back_inside_the_postroll_keeps_the_same_clip(self):
        planner = ClipPlanner(confirm=2, postroll=4.0)
        events = run(planner, [(0, 1), (0.5, 1), (1.0, 0), (3.0, 0), (3.5, 1), (4.0, 1), (4.5, 0), (7.9, 0), (8.0, 0)])
        # the person was away from 1.0 to 3.5 (2.5 s < postroll), so nothing stopped; last seen at 4.0 -> stop at 8.0
        self.assertEqual(events, [(0.5, "start"), (8.0, "stop")])

    def test_a_long_presence_rolls_over_into_the_next_clip(self):
        planner = ClipPlanner(confirm=2, postroll=4.0, max_clip=20.0)
        samples = [(i * 0.5, 1) for i in range(0, 90)]            # a person for 44.5 s
        events = [a for _, a in run(planner, samples)]
        self.assertEqual(events[0], "start")
        self.assertEqual(events.count("rollover"), 2)             # at 20.5 s and 40.5 s
        self.assertNotIn("stop", events)

    def test_no_rollover_when_the_person_has_gone_but_the_clip_is_old(self):
        planner = ClipPlanner(confirm=2, postroll=4.0, max_clip=5.0)
        events = run(planner, [(0, 1), (0.5, 1), (6.0, 0), (6.5, 0), (10.5, 0)])
        # at 6.0 the clip is older than max_clip but nobody is in view: it simply ends after the postroll
        self.assertEqual([a for _, a in events], ["start", "stop"])

    def test_reset_reports_whether_a_clip_was_open(self):
        planner = ClipPlanner(confirm=1)
        self.assertFalse(planner.reset())
        planner.observe(0, 1)
        self.assertTrue(planner.reset())
        self.assertEqual(planner.observe(1, 0), [])               # and the planner starts from scratch

    def test_after_a_stop_a_new_clip_needs_a_fresh_confirmation(self):
        planner = ClipPlanner(confirm=2, postroll=1.0)
        run(planner, [(0, 1), (0.5, 1), (2.0, 0)])                # started, then stopped
        self.assertEqual(run(planner, [(3.0, 1)]), [])            # one detection is not enough again
        self.assertEqual(run(planner, [(3.5, 1)]), [(3.5, "start")])


if __name__ == "__main__":
    unittest.main()
