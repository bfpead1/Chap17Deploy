from __future__ import annotations

import sys
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from jobs.ml_pipeline import train_and_save_model


def main() -> int:
    result = train_and_save_model()
    print(f"Model saved to: {result['model_path']}")
    print(f"Provider: {result['provider']}")
    print(f"Rows trained: {result['rows_trained']}")
    print(f"Features: {result['features']}")
    print(f"Threshold: {result['threshold']}")
    print(f"ROC AUC: {result['roc_auc']}")
    print(f"Average Precision: {result['average_precision']}")
    print(f"Recall: {result['recall']}")
    print(f"Precision: {result['precision']}")
    print(f"F1: {result['f1']}")
    print("Best params:")
    for key, value in result["best_params"].items():
        print(f"  {key} = {value}")
    print(result["report"])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
