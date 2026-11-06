"""Fixture metadata shared by the objective agent-parity runners and evaluators."""
from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

BASE = Path(__file__).resolve().parent
STATE_ROOT = Path("/home/philip/.cache/tmp/opencode/objective-agent-parity")


@dataclass(frozen=True)
class FixtureSpec:
    id: str
    root: Path
    baseline_path: Path
    private_dir: Path
    production_files: tuple[str, ...]
    visible_tests: tuple[str, ...]
    expected_failure: str
    prompt: str
    source_markers: tuple[str, ...]
    test_markers: tuple[str, ...]
    terminal_fixture: str
    ui_mode: str
    contract_mode: str
    baseline_validation: Path | None = None

    @property
    def workspace(self) -> Path:
        return STATE_ROOT / self.id

    @property
    def private_ids(self) -> tuple[str, ...]:
        return tuple(f"H{i:02d}" for i in range(1, 9))


FIXTURES: dict[str, FixtureSpec] = {
    "ENV-PARSER-001": FixtureSpec(
        id="ENV-PARSER-001",
        root=BASE / "fixture/ENV-PARSER-001",
        baseline_path=BASE / "baseline.json",
        private_dir=BASE / "private",
        production_files=("src/Fixture.Core/EnvFile.cs",),
        visible_tests=(
            "Fixture.Tests.EnvFileTests.StandardAssignmentsParseSuccessfully",
            "Fixture.Tests.EnvFileTests.CommentsAndBlankLinesAreIgnored",
            "Fixture.Tests.EnvFileTests.ValuesContainingAdditionalEqualsSignsArePreserved",
        ),
        expected_failure="Fixture.Tests.EnvFileTests.ValuesContainingAdditionalEqualsSignsArePreserved",
        prompt=(
            "The project contains a failing unit test. Investigate the failure, identify the underlying defect "
            "and make the smallest appropriate production-code correction. Preserve the documented behaviour, "
            "do not modify tests, and run `dotnet test` to verify the result. Report what you changed and whether "
            "the tests passed."
        ),
        source_markers=("EnvFile.cs", "src/Fixture.Core/EnvFile"),
        test_markers=("EnvFileTests.cs", "tests/Fixture.Tests"),
        terminal_fixture="objective-agent-progress.json",
        ui_mode="progress",
        contract_mode="single",
    ),
    "ENV-PARSER-002": FixtureSpec(
        id="ENV-PARSER-002",
        root=BASE / "fixture/ENV-PARSER-002",
        baseline_path=BASE / "baseline-002.json",
        private_dir=BASE / "private/ENV-PARSER-002",
        production_files=(
            "src/Fixture.Core/EnvFile.cs",
            "src/Fixture.Core/EnvInterpolator.cs",
        ),
        visible_tests=(
            "Fixture.Tests.EnvIntegrationTests.ParsesEmbeddedEqualsAndPreservesUnknownPlaceholders",
        ),
        expected_failure="Fixture.Tests.EnvIntegrationTests.ParsesEmbeddedEqualsAndPreservesUnknownPlaceholders",
        prompt=(
            "This .NET project contains a failing integration test. Investigate the implementation, identify the "
            "underlying defects and make the smallest appropriate corrections to production code. Preserve the "
            "documented behaviour, do not modify the tests, and run `dotnet test` to verify the result. Report what "
            "you changed and whether the tests passed."
        ),
        source_markers=("EnvFile.cs", "EnvInterpolator.cs", "src/Fixture.Core/"),
        test_markers=("EnvIntegrationTests.cs", "tests/Fixture.Tests"),
        terminal_fixture="objective-agent-recovery.json",
        ui_mode="recovery",
        contract_mode="recovery",
        baseline_validation=BASE / "baseline-002.json",
    ),
}

DEFAULT_FIXTURE_ID = "ENV-PARSER-001"


def get_fixture(fixture_id: str | None = None) -> FixtureSpec:
    key = fixture_id or DEFAULT_FIXTURE_ID
    try:
        return FIXTURES[key]
    except KeyError:
        raise SystemExit(f"Unknown fixture {key!r}; expected one of {', '.join(FIXTURES)}") from None
