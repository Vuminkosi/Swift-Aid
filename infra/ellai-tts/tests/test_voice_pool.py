import unittest
from pathlib import Path
from voice_pool import VoiceProfile, VoicePool, VoiceBusy, read_profiles


class VoicePoolTests(unittest.TestCase):
    def profile(self, name):
        return VoiceProfile(name, name, f"facebook/mms-tts-{name}", "a" * 40, "b" * 64, Path("/models") / name)

    def test_reuse_eviction_and_no_overlapping_inference(self):
        profiles = {key: self.profile(key) for key in ("eng", "sna", "tso")}
        loaded = []
        def load(profile):
            loaded.append(profile.language)
            return object()
        pool = VoicePool(profiles, load, 2)
        with pool.use("eng") as (_, english):
            with self.assertRaises(VoiceBusy):
                with pool.use("sna"):
                    pass
        with pool.use("sna"):
            pass
        with pool.use("eng") as (_, reused):
            self.assertIs(english, reused)
        with pool.use("tso"):
            self.assertEqual(["eng", "tso"], list(pool.cache))
        self.assertEqual(["eng", "sna", "tso"], loaded)

    def test_failed_load_releases_inference_owner(self):
        def load(_):
            raise RuntimeError("Unavailable")
        pool = VoicePool({"eng": self.profile("eng")}, load)
        for _ in range(2):
            with self.assertRaises(RuntimeError):
                with pool.use("eng"):
                    pass
        self.assertEqual(0, len(pool.cache))

    def test_default_inventory_is_english_only(self):
        self.assertEqual(["eng"], list(read_profiles("[]", self.profile("eng"))))

    def test_native_model_requires_matching_language_and_pins(self):
        import json
        entry = dict(language="sna", voiceId="shona-v1", modelId="facebook/mms-tts-sna",
                     revision="a" * 40, sha256="b" * 64, directory=str(Path.cwd()))
        self.assertIn("shona-v1", read_profiles(json.dumps([entry]), self.profile("eng")))
        for field, value in (("language", "ssw"), ("revision", "main"), ("sha256", ""), ("modelId", "facebook/mms-tts-eng")):
            with self.subTest(field=field), self.assertRaises(ValueError):
                read_profiles(json.dumps([{**entry, field: value}]), self.profile("eng"))
