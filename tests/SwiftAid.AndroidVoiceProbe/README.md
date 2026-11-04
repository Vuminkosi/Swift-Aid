# Android saved voice decoder probe

This standalone instrumentation app exercises the production Android saved-report voice decoder and WAV encoder with a generated sine wave. It does not use a microphone, network provider, SwiftAid application data, or a real recording. It checks AAC decoding, WAV format and non-silent samples, original-byte preservation, and pre-canceled decoding.

Select a disposable Android emulator and confirm `online.swiftaid.voiceprobe` is not already installed before using these commands. Replace `<serial>` with its serial from `adb devices -l`.

```powershell
dotnet build tests/SwiftAid.AndroidVoiceProbe/SwiftAid.AndroidVoiceProbe.csproj -c Debug -m:1 -nr:false
adb -s <serial> install tests/SwiftAid.AndroidVoiceProbe/bin/Debug/net10.0-android/online.swiftaid.voiceprobe-Signed.apk
adb -s <serial> shell am instrument -w online.swiftaid.voiceprobe/online.swiftaid.voiceprobe.Runner
adb -s <serial> uninstall online.swiftaid.voiceprobe
```

A successful run prints `probe=PASS` and `INSTRUMENTATION_CODE: -1`. This is a codec and conversion check, not a MAUI UI, microphone, live speech, or provider acceptance test.
