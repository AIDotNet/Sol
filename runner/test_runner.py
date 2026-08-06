import base64
import importlib.util
import pathlib
import sys
import tempfile
import unittest

MODULE_PATH = pathlib.Path(__file__).with_name("runner.py")
SPEC = importlib.util.spec_from_file_location("sol_skill_runner", MODULE_PATH)
runner = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
sys.modules[SPEC.name] = runner
SPEC.loader.exec_module(runner)


class RunnerTests(unittest.TestCase):
    def test_safe_relative_path_rejects_traversal_and_absolute_paths(self):
        for value in ("../run.py", "/run.py", "folder\\run.py", "./run.py"):
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    runner.safe_relative_path(value)

    def test_decoded_files_rejects_duplicate_and_oversized_packages(self):
        encoded = base64.b64encode(b"print('ok')").decode("ascii")
        with self.assertRaises(ValueError):
            runner.decoded_files([
                {"path": "run.py", "contentBase64": encoded},
                {"path": "run.py", "contentBase64": encoded},
            ])

        original = runner.MAX_PACKAGE_BYTES
        runner.MAX_PACKAGE_BYTES = 4
        try:
            with self.assertRaises(ValueError):
                runner.decoded_files([{"path": "run.py", "contentBase64": encoded}])
        finally:
            runner.MAX_PACKAGE_BYTES = original

    def test_package_writer_does_not_create_symlinks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory) / "skill"
            runner.write_package(root, [(pathlib.PurePosixPath("scripts/run.py"), b"print('ok')")])
            target = root / "scripts" / "run.py"
            self.assertEqual(b"print('ok')", target.read_bytes())
            self.assertFalse(target.is_symlink())
            self.assertEqual(0o400, target.stat().st_mode & 0o777)

    def test_bwrap_command_hides_runner_socket_and_unshares_network(self):
        command = runner.bwrap_command(
            pathlib.Path("/jobs/job/skill"),
            pathlib.Path("/jobs/job/work"),
            "/usr/bin/python3",
            pathlib.PurePosixPath("scripts/run.py"),
            ["one"],
        )
        self.assertIn("--unshare-net", command)
        self.assertIn("--clearenv", command)
        self.assertIn("--cap-drop", command)
        self.assertIn("--ro-bind", command)
        self.assertNotIn("/run/sol", command)
        self.assertEqual(["/usr/bin/python3", "/skill/scripts/run.py", "one"], command[-3:])

    def test_chroot_command_uses_fixed_helper_identity_and_interpreter(self):
        command = runner.chroot_command(
            pathlib.Path("/jobs/job/root"),
            20001,
            "/usr/bin/python3",
            pathlib.PurePosixPath("scripts/run.py"),
            ["one"],
            30,
        )
        self.assertEqual("/usr/bin/python3", command[0])
        self.assertEqual(runner.CHROOT_HELPER, command[1])
        self.assertEqual("20001", command[3])
        self.assertEqual("/usr/bin/python3", command[5])
        self.assertEqual("/skill/scripts/run.py", command[6])
        self.assertNotIn("/run/sol", command)


if __name__ == "__main__":
    unittest.main()
