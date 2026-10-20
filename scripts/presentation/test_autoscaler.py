import unittest
from autoscaler import Policy


class PolicyTests(unittest.TestCase):
    def test_sustained_pressure_scales_and_holds_during_cooldown(self):
        p = Policy()
        self.assertEqual(1, p.decide(20, 1, 0))
        self.assertEqual(2, p.decide(20, 1, 5))
        p.applied(5)
        for t in range(10, 65, 5):
            self.assertEqual(2, p.decide(0, 2, t))
        self.assertEqual(1, p.decide(0, 2, 65))

    def test_missing_metric_never_means_idle(self):
        p = Policy(down_samples=2)
        self.assertEqual(2, p.decide(0, 2, 0))
        self.assertEqual(2, p.decide(float('nan'), 2, 5))
        self.assertEqual(2, p.decide(0, 2, 10))
        self.assertEqual(1, p.decide(0, 2, 15))

    def test_short_burst_and_replica_ceiling(self):
        p = Policy()
        for value in [20, 0, 20, 0]:
            self.assertEqual(1, p.decide(value, 1, 100))
        for _ in range(20):
            self.assertEqual(2, p.decide(100, 2, 100))
            self.assertEqual(1, p.decide(0, 1, 100))


if __name__ == '__main__':
    unittest.main()
