import unittest

from NativeCollectionControl import MARKS, ProbeError, validate_command


class CollectionControlTests(unittest.TestCase):
    def test_only_known_player_actions_or_stop_are_accepted(self):
        for name in MARKS:
            command = {'id': 1, 'action': 'mark', 'name': name}
            self.assertEqual(validate_command(command, 0), command)
        self.assertEqual(validate_command({'id': 1, 'action': 'stop'}, 0)['action'], 'stop')

    def test_native_calls_extra_fields_and_unknown_marks_are_rejected(self):
        for command in ({'id': 1, 'action': 'invoke'}, {'id': 1, 'action': 'stop', 'pid': 11},
                        {'id': 1, 'action': 'mark', 'name': 'resume-preparation'},
                        {'id': True, 'action': 'stop'}, [], {}, {'id': -1}):
            with self.subTest(command=command), self.assertRaises(ProbeError):
                validate_command(command, 0)

    def test_old_commands_cannot_repeat_marks_or_stop(self):
        self.assertIsNone(validate_command({'id': 0, 'action': 'idle'}, 0))
        self.assertIsNone(validate_command({'id': 1, 'action': 'stop'}, 1))


if __name__ == '__main__':
    unittest.main()
