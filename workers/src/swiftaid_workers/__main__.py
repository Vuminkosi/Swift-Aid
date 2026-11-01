"""Command entry point for the SwiftAid worker package."""

import argparse
import logging
import os

from swiftaid_workers import __version__


def main() -> None:
    logging.basicConfig(level=os.getenv("SWIFTAID_LOG_LEVEL", "INFO").upper(),
                        format="%(asctime)s %(levelname)s %(name)s %(message)s")
    for noisy in ("httpx", "httpcore"):
        logging.getLogger(noisy).setLevel(logging.WARNING)
    for database in ("sqlalchemy", "psycopg", "asyncpg"):
        logging.getLogger(database).setLevel(logging.ERROR)
    parser = argparse.ArgumentParser(description="SwiftAid gateway-scoped job worker")
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--classification", action="store_true")
    parser.add_argument("--classification-backfill", type=str, metavar="JOB_UUID")
    args = parser.parse_args()
    if args.classification or args.classification_backfill:
        import asyncio
        from uuid import UUID

        from swiftaid_workers.classification.backfill import run_backfill
        from swiftaid_workers.classification.bart import BartClassifier
        from swiftaid_workers.classification.consumer import run_consumer
        from swiftaid_workers.classification.settings import ClassificationSettings
        job_id = str(UUID(args.classification_backfill)) if args.classification_backfill else None
        settings = ClassificationSettings()  # type: ignore[call-arg]
        if settings.health_file:
            from pathlib import Path
            Path(settings.health_file).unlink(missing_ok=True)
        classifier = BartClassifier(settings.device)
        classifier.load()
        logging.getLogger(__name__).info("Classification model loaded; starting %s", "backfill" if job_id else "consumer")
        if job_id:
            asyncio.run(run_backfill(settings, classifier, job_id))
        else:
            asyncio.run(run_consumer(settings, classifier))
        return
    parser.error("Select --classification and supply its scoped runtime configuration.")


if __name__ == "__main__":
    main()
