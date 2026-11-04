from pydantic import Field, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict


class ClassificationSettings(BaseSettings):
    model_config = SettingsConfigDict(env_prefix="CLASSIFICATION_")
    nats_url: str
    nats_user: str
    nats_password: str
    gateway_url: str
    worker_token: str = Field(min_length=32)
    durable: str = "swiftaid_classification_v1"
    concurrency: int = Field(default=2, ge=1, le=8)
    device: str = "cpu"
    allow_loopback_http: bool = False
    health_file: str | None = None
    backfill_interval_seconds: float = Field(default=1, ge=0.1, le=60)
    automatic_backfill_enabled: bool = False

    @model_validator(mode="after")
    def validate_transport(self) -> "ClassificationSettings":
        from urllib.parse import urlparse
        parsed = urlparse(self.gateway_url)
        if parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise ValueError("Gateway URL must not contain credentials or parameters")
        if parsed.scheme != "https" and not (self.allow_loopback_http and parsed.scheme == "http" and parsed.hostname in {"localhost", "127.0.0.1"}):
            raise ValueError("Gateway worker credentials require HTTPS except explicit loopback fixtures")
        return self
