"""Repository baseline checks for SwiftAid P0.1."""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class ScaffoldTests(unittest.TestCase):
    def test_required_projects_and_no_secret_defaults(self) -> None:
        required_paths = (
            "SwiftAid.sln",
            "global.json",
            "Directory.Packages.props",
            "infra/compose.yml",
            ".env.example",
            "src/SwiftAid.Gateway/SwiftAid.Gateway.csproj",
            "src/SwiftAid.Mobile/SwiftAid.Mobile.csproj",
            "workers/pyproject.toml",
        )
        missing = [path for path in required_paths if not (ROOT / path).is_file()]
        self.assertEqual([], missing, f"Missing baseline files: {missing}")

        project_files = list((ROOT / "src").rglob("*.csproj"))
        self.assertGreaterEqual(len(project_files), 7)

        floating_versions: list[str] = []
        for manifest in [*project_files, ROOT / "Directory.Packages.props"]:
            content = manifest.read_text(encoding="utf-8")
            if re.search(r"Version\s*=\s*['\"](?:\*|[^'\"]*\*)['\"]", content):
                floating_versions.append(str(manifest.relative_to(ROOT)))
        self.assertEqual([], floating_versions, "Dependencies must use fixed versions")

        example = (ROOT / ".env.example").read_text(encoding="utf-8")
        forbidden_secret_values = re.findall(
            r"(?im)^\s*(?:META_API_KEY|FACEBOOK_APP_SECRET|GOOGLE_CLIENT_SECRET|OIDC_CLIENT_SECRET)\s*=\s*(?!<|$)[^\s#]+",
            example,
        )
        self.assertEqual([], forbidden_secret_values, "Examples must not contain real secrets")

        package_manifests = list((ROOT / "src").rglob("*.csproj"))
        unlocked_projects = [
            str(path.relative_to(ROOT))
            for path in package_manifests
            if not path.with_name("packages.lock.json").is_file()
        ]
        self.assertEqual([], unlocked_projects, "Each project needs a committed NuGet lock file")

        dockerignore = (ROOT / ".dockerignore").read_text(encoding="utf-8").splitlines()
        required_exclusions = {
            ".env",
            ".env.*",
            "**/*.pem",
            "**/*.key",
            "**/*.pfx",
            "**/secrets/**",
            "designs/",
            "**/private-media/**",
            "**/bin/",
            "**/obj/",
            "**/.venv/",
        }
        self.assertTrue(required_exclusions.issubset(dockerignore))
        for required_input in (
            "global.json",
            "Directory.Build.props",
            "Directory.Packages.props",
            "SwiftAid.sln",
            "src/",
        ):
            self.assertNotIn(required_input, dockerignore)

    def test_plan_tasks_are_defined_by_headings(self) -> None:
        plan = (ROOT / "implementation.md").read_text(encoding="utf-8")
        task_heading_ids = re.findall(r"(?m)^#### (P\d+\.\d+) — ", plan)
        task_reference_ids = set(re.findall(r"\bP\d+\.\d+\b", plan))

        self.assertTrue(task_heading_ids, "Implementation task headings must be present")
        self.assertEqual(len(task_heading_ids), len(set(task_heading_ids)))
        self.assertEqual(set(task_heading_ids), task_reference_ids)
        self.assertIn("P0.1", task_reference_ids)
        self.assertIn("P8.6", task_reference_ids)

    def test_ui_framework_owner_decision_remains_gated(self) -> None:
        ui_components = (ROOT / "docs" / "UI-COMPONENTS.md").read_text(encoding="utf-8")

        self.assertIn("**MudBlazor is primary**", ui_components)
        self.assertIn("**Syncfusion is reserved for justified complex specialized components**", ui_components)
        self.assertIn("conditional; do not use for ordinary controls", ui_components.lower())
        self.assertIn("no package/license evidence observed", ui_components.lower())
        self.assertIn("license eligibility", ui_components.lower())
        self.assertIn("compatibility is unresolved", ui_components.lower())
        self.assertIn("keep a functional mudblazor fallback", ui_components.lower())


if __name__ == "__main__":
    unittest.main()
