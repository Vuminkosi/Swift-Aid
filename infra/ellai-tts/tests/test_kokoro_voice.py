import hashlib
from pathlib import Path

import pytest

from kokoro_voice import KokoroVoice, verify_file


def test_artifact_verification_rejects_missing_or_modified_files(tmp_path):
    artifact = tmp_path / "voice.pt"
    digest = hashlib.sha256(b"test voice").hexdigest()
    with pytest.raises(FileNotFoundError):
        verify_file(artifact, digest)
    artifact.write_bytes(b"test voice")
    verify_file(artifact, digest)
    artifact.write_bytes(b"modified voice")
    with pytest.raises(ValueError):
        verify_file(artifact, digest)


@pytest.mark.parametrize("voice", ["../af_heart", "sna", "af_heart.pt", "jf_alpha", ""])
def test_rejects_paths_and_non_english_voices_before_model_loading(voice):
    with pytest.raises(ValueError):
        KokoroVoice(Path("missing"), voice)
