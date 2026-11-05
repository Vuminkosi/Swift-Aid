"""Configuration and readiness boundaries; no live database or production mutations."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from config import gateway_environment, native_profiles, organization_ids, validate_provider
from health import healthy

ORG = '11111111-1111-4111-8111-111111111111'


class DeploymentTests(unittest.TestCase):
    def test_scope_cannot_be_missing_wildcard_or_empty_uuid(self):
        for scope in ('', '*', '00000000-0000-0000-0000-000000000000', 'not-a-uuid'):
            with self.subTest(scope=scope), self.assertRaises(ValueError):
                organization_ids(scope)

    def test_scope_deduplicates_and_caps_input(self):
        self.assertEqual([ORG], organization_ids(f'{ORG},{ORG}'))
        with self.assertRaises(ValueError):
            organization_ids(','.join([ORG] * 101))

    def test_production_wiring_overrides_insecure_inherited_settings(self):
        supplied = {'Ellai__Enabled': 'false', 'RUNPOD_CLASSIFICATION_ORGANIZATIONS': ORG,
                    'RUNPOD_POD_ID': 'example', 'ASPNETCORE_ENVIRONMENT': 'Development',
                    'TransportSecurity__AllowPlaintextDevelopment': 'true',
                    'Classification__Organizations__99': 'other-tenant'}
        secrets = {key: 'a' * 64 for key in ('certificate_password', 'database_app', 'redis', 'qdrant',
                   'nats_gateway', 'minio_app', 'classification', 'tts', 'language')}
        env = gateway_environment(Path('/workspace/swiftaid'), secrets, supplied)
        self.assertEqual('Production', env['ASPNETCORE_ENVIRONMENT'])
        self.assertEqual('false', env['TransportSecurity__AllowPlaintextDevelopment'])
        self.assertEqual('false', env['Authentication__AllowInsecureDevelopmentCookies'])
        self.assertNotIn('Classification__Organizations__99', env)
        self.assertEqual(ORG, env['Classification__Organizations__0'])
        self.assertIn('example-8080.proxy.runpod.net', env['AllowedHosts'])
        self.assertIn('SSL Mode=VerifyFull', env['ConnectionStrings__SwiftAid'])
        self.assertNotIn('Username=postgres', env['ConnectionStrings__SwiftAid'])
        self.assertEqual('false', env['DemoAccount__Enabled'])
        self.assertEqual('false', env['DemoData__Enabled'])
        self.assertEqual('true', env['Identity__Enabled'])

    def test_provider_rejects_missing_and_plaintext_credentials(self):
        with self.assertRaises(ValueError):
            validate_provider({})
        provider = {'Ellai__ApiKey': 'secret', 'Ellai__ASR_MODEL': 'model',
                    'Ellai__ASR_WSS': 'wss://voice.example/ws',
                    'Ellai__ASR_Transcription__URL': 'https://voice.example/transcribe',
                    'Ellai__ApiUrl': 'https://reason.example/api', 'Ellai__ReasoningModel': 'reason'}
        validate_provider(provider)
        for url in ('http://reason.example/api', 'https://user:password@reason.example/api'):
            with self.subTest(url=url), self.assertRaises(ValueError):
                validate_provider(dict(provider, Ellai__ApiUrl=url))

    def test_deployment_retains_hosted_muse(self):
        with self.assertRaises(ValueError):
            validate_provider({'Ellai__ReasoningBackend': 'ollama'})

    def test_native_inventory_uses_existing_revisions_and_all_three_languages(self):
        manifest = json.loads((ROOT.parents[1] / 'scripts/ellai/native-models.lock.json').read_text())
        profiles = json.loads(native_profiles(Path('/workspace/swiftaid'), manifest))
        self.assertEqual({'sna', 'nya', 'tso'}, {item['language'] for item in profiles})
        self.assertTrue(all(item['revision'] in item['directory'] for item in profiles))

    def test_readiness_rejects_stale_partial_and_malformed_status(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'health.json'
            self.assertFalse(healthy(path, 100))
            for state in ({'checkedAt': 99, 'ready': True, 'services': {}},
                          {'checkedAt': 99, 'ready': True, 'services': {'gateway': True, 'database': False}},
                          {'checkedAt': 99, 'ready': True, 'services': {'gateway': 'true'}},
                          {'checkedAt': 30, 'ready': True, 'services': {'gateway': True}},
                          {'checkedAt': 101, 'ready': True, 'services': {'gateway': True}}):
                path.write_text(json.dumps(state))
                self.assertFalse(healthy(path, 100), state)
            path.write_text(json.dumps({'checkedAt': 99, 'ready': True, 'services': {'gateway': True, 'classification': True}}))
            self.assertTrue(healthy(path, 100))
            path.write_text('not-json')
            self.assertFalse(healthy(path, 100))

    def test_additional_models_match_runtime_pinned_hashes(self):
        manifest = json.loads((ROOT / 'models.lock.json').read_text())
        kokoro_source = (ROOT.parent / 'ellai-tts/kokoro_voice.py').read_text()
        kokoro = next(model for model in manifest['models'] if model['model'] == 'hexgrad/Kokoro-82M')
        for artifact in kokoro['files']:
            # Runtime validates these exact SHA256 values, including the non-LFS config file.
            if artifact.get('sha256'):
                self.assertIn(artifact['sha256'], kokoro_source)


if __name__ == '__main__':
    unittest.main()
