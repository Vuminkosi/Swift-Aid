"""Command entry point for the SwiftAid worker package."""

import argparse

from swiftaid_workers import __version__


def main() -> None:
    parser = argparse.ArgumentParser(description="SwiftAid gateway-scoped job worker")
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--classification", action="store_true")
    args = parser.parse_args()
    if args.classification:
        import asyncio

        from swiftaid_workers.classification.bart import BartClassifier
        from swiftaid_workers.classification.consumer import run_consumer
        from swiftaid_workers.classification.settings import ClassificationSettings
        settings = ClassificationSettings()  # type: ignore[call-arg]
        classifier = BartClassifier(settings.device)
        classifier.load()
        asyncio.run(run_consumer(settings, classifier))
        return
    parser.error("Select --classification and supply its scoped runtime configuration.")


if __name__ == "__main__":
    main()
