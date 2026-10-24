import sys
from pathlib import Path

# Permet `pytest` depuis n'importe où : le paquet `app` est à la racine du dépôt.
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
