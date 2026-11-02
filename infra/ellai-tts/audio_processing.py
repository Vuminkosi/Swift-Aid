"""Optional synthesis-only EQ. Never used on microphone audio or transport frames."""
from dataclasses import dataclass
import json
import math
import re

import numpy as np
from scipy.signal import butter, sosfilt


@dataclass(frozen=True)
class ClarityProfile:
    enabled: bool = False
    high_pass_hz: float = 80
    low_shelf_hz: float = 180
    low_shelf_db: float = -3
    presence_hz: float = 3000
    presence_db: float = 1.5
    ceiling_db: float = -1

    def __post_init__(self):
        if type(self.enabled) is not bool:
            raise ValueError("Clarity enabled must be a boolean.")
        bounds = {"high_pass_hz": (20, 200), "low_shelf_hz": (80, 500),
                  "low_shelf_db": (-6, 0), "presence_hz": (1000, 6000),
                  "presence_db": (0, 3), "ceiling_db": (-6, -0.1)}
        for name, (low, high) in bounds.items():
            value = getattr(self, name)
            if type(value) not in (int, float) or not math.isfinite(value) or not low <= value <= high:
                raise ValueError(f"Invalid clarity parameter: {name}.")


def read_profiles(raw: str) -> dict[str, ClarityProfile]:
    entries = json.loads(raw)
    if not isinstance(entries, dict) or len(entries) > 16:
        raise ValueError("Clarity profiles must be a bounded language map.")
    result = {}
    for language, values in entries.items():
        if not re.fullmatch(r"[a-z]{3}", language) or not isinstance(values, dict):
            raise ValueError("Invalid clarity language profile.")
        try:
            result[language] = ClarityProfile(**values)
        except TypeError as error:
            raise ValueError("Unknown clarity parameter.") from error
    return result


def coefficients(rate: int, profile: ClarityProfile) -> np.ndarray:
    if not 8000 <= rate <= 48000 or max(profile.high_pass_hz, profile.low_shelf_hz, profile.presence_hz) >= rate / 2:
        raise ValueError("Clarity frequencies must be below the PCM Nyquist frequency.")
    # RBJ / W3C Audio EQ Cookbook: shelf slope S=1, presence Q=1.
    a = 10 ** (profile.low_shelf_db / 40)
    w = 2 * math.pi * profile.low_shelf_hz / rate
    c, s = math.cos(w), math.sin(w)
    beta = math.sqrt(2 * a) * s
    shelf = np.array([a * ((a + 1) - (a - 1) * c + beta),
                      2 * a * ((a - 1) - (a + 1) * c),
                      a * ((a + 1) - (a - 1) * c - beta),
                      (a + 1) + (a - 1) * c + beta,
                      -2 * ((a - 1) + (a + 1) * c),
                      (a + 1) + (a - 1) * c - beta])
    shelf /= shelf[3]
    a = 10 ** (profile.presence_db / 40)
    w = 2 * math.pi * profile.presence_hz / rate
    alpha, c = math.sin(w) / 2, math.cos(w)
    presence = np.array([1 + alpha * a, -2 * c, 1 - alpha * a,
                         1 + alpha / a, -2 * c, 1 - alpha / a])
    presence /= presence[3]
    return np.vstack([butter(2, profile.high_pass_hz, btype="highpass", fs=rate, output="sos"), shelf, presence])


def process(waveform: np.ndarray, rate: int, profile: ClarityProfile) -> np.ndarray:
    if waveform.ndim != 1 or waveform.size == 0 or not np.isfinite(waveform).all():
        raise ValueError("Invalid synthesized waveform.")
    if not profile.enabled:
        return waveform
    output = sosfilt(coefficients(rate, profile), waveform)
    # One constant attenuation per utterance avoids limiter pumping and hard clipping.
    # This is a sample-peak ceiling, not a reconstructed analogue true-peak guarantee.
    peak = float(np.max(np.abs(output)))
    ceiling = 10 ** (profile.ceiling_db / 20)
    if peak > ceiling:
        output *= ceiling / peak
    if not np.isfinite(output).all():
        raise ValueError("Invalid processed waveform.")
    return output
