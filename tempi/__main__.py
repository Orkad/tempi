"""Point d'entrée pour ``python -m tempi``."""

import sys

from .cli import main

if __name__ == "__main__":
    sys.exit(main())
