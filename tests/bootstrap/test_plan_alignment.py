"""Guard the root specification/implementation pairing and task coverage."""

import hashlib
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]


class PlanAlignmentTests(unittest.TestCase):
    def test_spec_plan_ids_and_scope_preserved(self) -> None:
        spec_path = ROOT / "spec.md"
        plan_path = ROOT / "implementation.md"
        self.assertTrue(spec_path.is_file())
        self.assertTrue(plan_path.is_file())

        spec = spec_path.read_text(encoding="utf-8")
        plan = plan_path.read_text(encoding="utf-8")
        self.assertRegex(spec, r"(?m)^\| Version \| 1\.4\b")
        self.assertRegex(plan, r"(?m)^\*\*Plan revision:\*\* \*\*2\.4\b")
        self.assertEqual(
            "8900b57ea08c9db0704d26b19ab6289d4e4d7891d2609cc253caf1d3a66b4aee",
            hashlib.sha256(spec_path.read_bytes()).hexdigest(),
        )
        self.assertEqual(
            "09f9eb36683d8860d2904cb1604c6a5c65e7ec9c51a9380e3bfef46a4a1f2023",
            hashlib.sha256(plan_path.read_bytes()).hexdigest(),
        )

        requirement_ids = set(re.findall(r"\bSA-[A-Z]+-\d{2,3}\b", spec))
        scenario_ids = set(re.findall(r"\bAT-\d{2}\b", spec))
        self.assertEqual(365, len(requirement_ids))
        self.assertEqual(76, len(scenario_ids))

        task_ids = set(re.findall(r"\bP\d+\.\d+\b", plan))
        self.assertEqual(44, len(task_ids))
        self.assertIn("P8.6", task_ids)
        self.assertEqual(
            {f"SA-LOOP-{number:02}" for number in range(1, 16)},
            {requirement_id for requirement_id in requirement_ids if requirement_id.startswith("SA-LOOP-")},
        )
        self.assertTrue((ROOT / "docs" / "SPEC-ALIGNMENT.md").is_file())
        self.assertTrue((ROOT / "docs" / "TASK-OWNERS.md").is_file())
        self.assertTrue((ROOT / "docs" / "TRACEABILITY.md").is_file())
        self.assertTrue((ROOT / "docs" / "ACCEPTANCE.md").is_file())

        alignment = (ROOT / "docs" / "SPEC-ALIGNMENT.md").read_text(encoding="utf-8")
        self.assertIn("MudBlazor", alignment)
        self.assertIn("Syncfusion", alignment)
        for clarification in range(1, 9):
            self.assertIn(f"PC-{clarification:02}", alignment)

        for document in (spec, plan):
            self.assertNotIn("OpenAI health assistant", document)
        self.assertIn("AI diagnosis", spec)
        self.assertIn("MUST NOT generate medical/veterinary advice", spec)
        self.assertIn("P2.4", plan)
        self.assertIn("P7.4", plan)
        self.assertIn("P8.5", plan)
        self.assertIn("P8.6", plan)
        self.assertIn("P10.3", plan)


if __name__ == "__main__":
    unittest.main()
