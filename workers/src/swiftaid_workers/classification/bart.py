"""Pinned local MNLI inference. Scores describe semantic entailment, never clinical risk."""
from __future__ import annotations

import asyncio
import time
from typing import Any

MODEL_ID = "facebook/bart-large-mnli"
MODEL_REVISION = "d7645e127eaf1aefc7862fd59a17a5aa8558b8ce"


class BartClassifier:
    def __init__(self, device: str = "cpu", pair_batch_size: int = 4, token_budget: int = 2048) -> None:
        if device not in {"cpu", "cuda"} or not 1 <= pair_batch_size <= 16 or not 1024 <= token_budget <= 8192:
            raise ValueError("invalid-inference-profile")
        self.device = device
        self.pair_batch_size = pair_batch_size
        self.token_budget = token_budget
        self.model: Any = None
        self.tokenizer: Any = None
        self.gate = asyncio.Semaphore(1)

    def load(self) -> None:
        import torch
        from transformers import AutoModelForSequenceClassification, AutoTokenizer

        torch.set_num_threads(4)
        self.tokenizer = AutoTokenizer.from_pretrained(MODEL_ID, revision=MODEL_REVISION, trust_remote_code=False)
        self.model = AutoModelForSequenceClassification.from_pretrained(MODEL_ID, revision=MODEL_REVISION,
            use_safetensors=True, trust_remote_code=False).to(self.device).eval()
        if self.model.config.label2id != {"contradiction": 0, "neutral": 1, "entailment": 2}:
            raise ValueError("unsupported-mnli-label-mapping")

    async def classify(self, lease: dict[str, Any]) -> dict[str, Any]:
        async with self.gate:
            work = asyncio.create_task(asyncio.to_thread(self._classify, lease))
            try:
                return await asyncio.shield(work)
            except asyncio.CancelledError:
                # Python cannot cancel native inference. Retain the semaphore until it exits.
                await work
                raise

    def _classify(self, lease: dict[str, Any]) -> dict[str, Any]:
        profile = lease["profile"]
        if profile["modelId"] != MODEL_ID or profile["modelRevision"] != MODEL_REVISION:
            raise ValueError("unsupported-model-revision")
        started = time.perf_counter()
        result = {key: lease[key] for key in ("resultId", "leaseId", "configurationHash", "fingerprint")}
        result.update(predictions=[], abstentionReason=None, inputTokens=0, inferenceMilliseconds=0.0)
        if profile["sourceLanguage"] != "en":
            result["abstentionReason"] = "unsupported-language"
            return result
        if not lease["text"]:
            result["abstentionReason"] = "empty-input"
            return result
        if self.model is None:
            self.load()
        import torch

        text = lease["text"]
        labels = profile["labels"]
        if not 1 <= len(labels) <= 32 or len(text) > 32000:
            raise ValueError("invalid-inference-input")
        maximum = profile["maximumTokens"]
        pairs = []
        for label in labels:
            hypothesis = profile["hypothesisTemplate"].replace("{}", label["description"])
            encoded = self.tokenizer(text, hypothesis, truncation=False)
            length = len(encoded["input_ids"])
            if length > maximum:
                result["abstentionReason"] = "input-too-long"
                return result
            pairs.append((length, label["id"], encoded))
        pairs.sort(key=lambda pair: pair[0])
        predictions: list[dict[str, Any]] = []
        offset = 0
        while offset < len(pairs):
            batch: list[tuple[int, str, Any]] = []
            while offset < len(pairs) and len(batch) < self.pair_batch_size:
                candidate = pairs[offset]
                if batch and candidate[0] * (len(batch) + 1) > self.token_budget:
                    break
                batch.append(candidate)
                offset += 1
            inputs = self.tokenizer.pad([pair[2] for pair in batch], padding=True, return_tensors="pt").to(self.device)
            with torch.inference_mode():
                logits = self.model(**inputs).logits
                scores = logits[:, [0, 2]].softmax(dim=1)[:, 1].cpu().tolist()
            predictions.extend({"labelId": pair[1], "score": score} for pair, score in zip(batch, scores, strict=True))
        result.update(predictions=sorted(predictions, key=lambda p: p["labelId"]),
                      inputTokens=max(pair[0] for pair in pairs), inferenceMilliseconds=(time.perf_counter() - started) * 1000)
        return result
