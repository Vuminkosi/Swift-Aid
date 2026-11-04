import numpy as np
import pytest

from audio_processing import ClarityProfile, process, read_profiles


@pytest.mark.parametrize("rate", [8000, 16000, 22050, 48000])
def test_frequency_response_and_duration(rate):
    profile = ClarityProfile(enabled=True)
    for frequency, lower, upper in [(20, -30, -24), (180, -2.2, -1.2), (3000, 1.4, 1.6)]:
        source = .1 * np.sin(2 * np.pi * frequency * np.arange(rate) / rate)
        output = process(source, rate, profile)
        assert len(output) == len(source)
        gain = 20 * np.log10(np.linalg.norm(output[rate // 2:]) / np.linalg.norm(source[rate // 2:]))
        assert lower < gain < upper


def test_bypass_is_exact_and_does_not_mutate_input():
    source = np.array([0, .5, -.5], dtype=np.float32)
    assert process(source, 16000, ClarityProfile()) is source
    process(source, 16000, ClarityProfile(enabled=True))
    np.testing.assert_array_equal(source, [0, .5, -.5])


def test_ceiling_silence_and_finite_values():
    profile = ClarityProfile(enabled=True)
    output = process(np.random.default_rng(42).normal(size=16000), 16000, profile)
    assert np.max(np.abs(output)) <= 10 ** (-1 / 20) + 1e-12
    assert np.all(process(np.zeros(16000), 16000, profile) == 0)
    with pytest.raises(ValueError):
        process(np.array([np.nan]), 16000, profile)


def test_language_profiles_and_invalid_configuration():
    profiles = read_profiles('{"eng":{"enabled":true},"sna":{"enabled":false,"presence_db":0}}')
    assert profiles["eng"].enabled
    assert not profiles["sna"].enabled
    for value in ['[]', '{"en":{}}', '{"eng":{"unknown":1}}', '{"eng":{"enabled":"false"}}', '{"eng":{"ceiling_db":1}}']:
        with pytest.raises(ValueError):
            read_profiles(value)


def test_rejects_invalid_rate_and_nyquist_configuration():
    with pytest.raises(ValueError):
        process(np.zeros(100), 4000, ClarityProfile(enabled=True))
    with pytest.raises(ValueError):
        process(np.zeros(100), 8000, ClarityProfile(enabled=True, presence_hz=5000))
