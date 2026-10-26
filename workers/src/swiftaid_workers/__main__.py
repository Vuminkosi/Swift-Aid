"""Command entry point for the SwiftAid worker package."""

import argparse

from swiftaid_workers import __version__


def main() -> None:
    parser = argparse.ArgumentParser(description="SwiftAid gateway-scoped job worker")
    parser.add_argument("--version", action="version", version=__version__)
    parser.parse_args()
    parser.error("No job consumers are enabled in P0.1; see implementation task P6.2.")


if __name__ == "__main__":
    main()
