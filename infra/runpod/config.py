"""Pure configuration assembly shared with deployment contract tests."""
from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path
from urllib.parse import urlparse
from uuid import UUID


def organization_ids(value: str) -> list[str]:
    values = [part.strip() for part in value.split(",") if part.strip()]
    if not values or len(values) > 100:
        raise ValueError("Set RUNPOD_CLASSIFICATION_ORGANIZATIONS to 1-100 authorized organization UUIDs")
    try:
        result = list(dict.fromkeys(str(UUID(value)) for value in values))
    except ValueError:
        raise ValueError("Classification organization scope must contain UUIDs") from None
    if str(UUID(int=0)) in result:
        raise ValueError("Empty organization UUID is not an authorization scope")
    return result


def validate_provider(environment: dict[str, str]) -> None:
    if environment.get("Ellai__Enabled", "true").lower() != "true":
        return
    backend = environment.get("Ellai__ReasoningBackend", "meta")
    if backend != "meta":
        raise ValueError("This deployment uses hosted Muse reasoning; set Ellai__ReasoningBackend=meta")
    required = {"Ellai__ApiKey": None, "Ellai__ASR_MODEL": None,
                "Ellai__ASR_WSS": "wss", "Ellai__ASR_Transcription__URL": "https"}
    required.update({"Ellai__ApiUrl": "https", "Ellai__ReasoningModel": None})
    for name, scheme in required.items():
        value = environment.get(name, "")
        if not value.strip():
            raise ValueError(f"Set {name}; hosted Muse assets cannot be downloaded into the Pod")
        if scheme:
            uri = urlparse(value)
            if uri.scheme != scheme or not uri.hostname or uri.username or uri.password:
                raise ValueError(f"{name} requires a credential-free {scheme} URL")


def gateway_environment(state: Path, secrets: dict[str, str], supplied: dict[str, str]) -> dict[str, str]:
    organizations = organization_ids(supplied.get("RUNPOD_CLASSIFICATION_ORGANIZATIONS", ""))
    validate_provider(supplied)
    public = supplied.get("SWIFTAID_PUBLIC_HOST", "")
    if not public and supplied.get("RUNPOD_POD_ID"):
        public = f"{supplied['RUNPOD_POD_ID']}-8080.proxy.runpod.net"
    if (not public or len(public) > 253 or any(not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?", label)
                                             for label in public.split('.'))):
        raise ValueError("Set SWIFTAID_PUBLIC_HOST to a hostname, or supply RUNPOD_POD_ID")
    if supplied.get("DemoAccount__Enabled", "false").lower() == "true" and len(supplied.get("DemoAccount__Password", "")) < 12:
        raise ValueError("Enabled demo provisioning requires an explicit DemoAccount__Password of at least 12 characters")
    security = state / "security"
    env = dict(supplied)
    env.update({
        "ASPNETCORE_ENVIRONMENT": "Production", "ASPNETCORE_URLS": "https://127.0.0.1:8443",
        "Kestrel__Certificates__Default__Path": str(security / "server.pfx"),
        "Kestrel__Certificates__Default__Password": secrets["certificate_password"],
        "AllowedHosts": f"localhost;127.0.0.1;{public}",
        "PublicSite__BaseUrl": f"https://{public}",
        "SwiftAid__GatewayBaseAddress": "https://localhost:8443",
        "Identity__Enabled": supplied.get("Identity__Enabled", "true"),
        "Identity__Issuer": supplied.get("Identity__Issuer", f"https://{public}/"),
        "Identity__SigningCertificatePath": str(security / "identity-signing.pfx"),
        "Identity__SigningCertificatePassword": secrets["certificate_password"],
        "Identity__EncryptionCertificatePath": str(security / "identity-encryption.pfx"),
        "Identity__EncryptionCertificatePassword": secrets["certificate_password"],
        "Authentication__AllowInsecureDevelopmentCookies": "false",
        "TransportSecurity__AllowPlaintextDevelopment": "false",
        "ConnectionStrings__SwiftAid": connection_string(secrets["database_app"], "swiftaid_app"),
        "CaseCommandProcessing__Enabled": "true", "DataProtection__Enabled": "true",
        "DataProtection__ApplicationName": "SwiftAid", "DataProtection__KeysPath": str(state / "keys"),
        "DataProtection__MaximumPayloadBytes": "65536", "DataProtection__AllowUnencryptedDevelopmentKeys": "false",
        "DataProtection__CertificatePath": str(security / "protection.pfx"),
        "DataProtection__CertificatePassword": secrets["certificate_password"],
        "Redis__Enabled": "true", "Redis__InstanceName": "swiftaid:",
        "Redis__ConnectionString": f"localhost:6379,password={secrets['redis']},ssl=true,sslHost=localhost,abortConnect=false",
        "Qdrant__Enabled": "true", "Qdrant__Endpoint": "https://localhost:6333", "Qdrant__ApiKey": secrets["qdrant"],
        "Nats__Enabled": "true", "Nats__Url": "tls://localhost:4222", "Nats__Username": "gateway",
        "Nats__Password": secrets["nats_gateway"], "Nats__IngestionDispatchEnabled": "true",
        "Minio__Enabled": "true", "Minio__Endpoint": "https://localhost:9000", "Minio__UseSsl": "true",
        "Minio__Bucket": "swiftaid-private-media", "Minio__MaxObjectSizeBytes": "104857600",
        "Minio__AccessKey": "swiftaid-app", "Minio__SecretKey": secrets["minio_app"],
        "Classification__Enabled": "true", "Classification__WorkerId": "classification-worker",
        "Classification__WorkerTokenSha256": hashlib.sha256(secrets["classification"].encode()).hexdigest(),
        "EllaiTts__Endpoint": "http://127.0.0.1:8090/", "EllaiTts__InternalKey": secrets["tts"],
        "EllaiLanguage__Endpoint": "http://127.0.0.1:8091/", "EllaiLanguage__InternalKey": secrets["language"],
        "Ellai__Policy__Path": supplied.get("Ellai__Policy__Path", "/etc/swiftaid/policy/ellai-system-instruction.md"),
        "Ellai__Docs__Path": supplied.get("Ellai__Docs__Path", "/etc/swiftaid/docs/TECHNICAL.md"),
        "Ellai__Policy__Version": "1.6", "Ellai__Enabled": supplied.get("Ellai__Enabled", "true"),
        "Ellai__ReasoningBackend": supplied.get("Ellai__ReasoningBackend", "meta"),
        "DemoAccess__Enabled": supplied.get("DemoAccess__Enabled", "false"),
        "DemoAccount__Enabled": supplied.get("DemoAccount__Enabled", "false"),
        "DemoData__Enabled": supplied.get("DemoData__Enabled", "false"),
        "Logging__Console__FormatterName": "simple",
        "Logging__Console__FormatterOptions__SingleLine": "true",
    })
    # Discard accidental inherited extra array entries; only the explicit deployment scope is admitted.
    env = {key: value for key, value in env.items() if not key.lower().startswith("classification__organizations__")}
    env.update({f"Classification__Organizations__{i}": value for i, value in enumerate(organizations)})
    return env


def connection_string(password: str, user: str) -> str:
    return f"Host=localhost;Port=5432;Database=swiftaid;Username={user};Password={password};SSL Mode=VerifyFull"


def native_profiles(state: Path, manifest: dict) -> str:
    profiles = []
    for model in manifest["models"]:
        if model["model"].startswith("facebook/mms-tts-"):
            language = model["model"].rsplit("-", 1)[1]
            weight = next(item for item in model["files"] if item["name"] == "model.safetensors")
            profiles.append(dict(voiceId=f"swiftaid-mms-{language}-v1", language=language,
                                 modelId=model["model"], revision=model["revision"], sha256=weight["sha256"],
                                 directory=str(state / "models" / model["model"].split("/")[1] / model["revision"])))
    if {profile["language"] for profile in profiles} != {"sna", "nya", "tso"}:
        raise ValueError("Incomplete native speech inventory")
    return json.dumps(profiles)
