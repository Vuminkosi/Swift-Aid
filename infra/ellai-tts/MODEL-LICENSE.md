# Ellai Gateway speech model notice

The optional Gateway voice profile uses Meta AI's `facebook/mms-tts-eng` English checkpoint.

- Model card: <https://huggingface.co/facebook/mms-tts-eng>
- Immutable repository revision: `c71de0fe7204c83f1c10820a7d696d0b450048ba`
- `model.safetensors` SHA-256: `69cf8b651c1493f1801dfd2311c298d694a38357bc9a1e41f410491ea6f0e1be`
- License: [CC BY-NC 4.0](https://creativecommons.org/licenses/by-nc/4.0/)
- Attribution: Meta AI, “Massively Multilingual Speech: Text-to-Speech Models,” model `facebook/mms-tts-eng`, based on Pratap et al., *Scaling Speech Technology to 1,000+ Languages* (2023).

SwiftAid's owner approved noncommercial use only. The model stays in a dedicated Docker volume and is not copied into the Gateway application image or distributed as part of public releases. Provide this attribution and license link in the product's model notices when the voice profile is enabled. Do not imply Meta endorsement. No fine-tuning, voice cloning, emotional conditioning, or commercial use is authorized by this notice.

The runtime loads only the mounted, pinned checkpoint in offline mode and checks the weights file SHA-256 before loading. The compose profile remains opt-in. Do not enable it for commercial activity or distribute the model artifact.
