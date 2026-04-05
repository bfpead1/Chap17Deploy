from __future__ import annotations

import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from jobs.ml_pipeline import score_and_write_predictions


def main() -> int:
    result = score_and_write_predictions()
    print(result["message"])
    print(f"{result['orders_scored']} orders scored")
    print(f"Provider: {result['provider']}")
    print(f"Threshold: {result.get('threshold', 'n/a')}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
