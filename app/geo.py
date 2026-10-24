"""Géocodage inverse hors ligne (tri des photos par lieu), d'après GeoNames.

Le pont envoie les coordonnées GPS lues dans l'EXIF ; on répond ville, département,
région et pays sans appeler aucun service tiers : les positions des photos de Yayo ne
quittent pas le serveur, et ne sont ni conservées ni journalisées.

Données : `cities500` (tous les lieux habités de plus de 500 habitants, ~230 000) et les
noms des divisions administratives, téléchargés une fois dans `DATA_DIR/geonames/`.
En mémoire, pas de dict par lieu (≈ 200 octets chacun) : des tableaux parallèles triés
par case de grille, et un dict case → tranche de ces tableaux.
"""

import io
import logging
import math
import os
import re
import sys
import threading
import time
import urllib.request
import uuid
import zipfile
from array import array
from pathlib import Path
from typing import Any, Callable, Iterable

log = logging.getLogger("yayoextractor.geo")

GEONAMES_URL = "https://download.geonames.org/export/dump/"
CITIES_ZIP = "cities500.zip"
CITIES_TXT = "cities500.txt"
ADMIN1_TXT = "admin1CodesASCII.txt"
ADMIN2_TXT = "admin2Codes.txt"
FILES = (CITIES_ZIP, ADMIN1_TXT, ADMIN2_TXT)

MAX_POINTS = 5000
CITY_KM = 30.0
COUNTRY_KM = 100.0
KM_PER_DEG = 111.195  # un degré de latitude (rayon terrestre moyen 6371 km)
CELL_DEG = 0.2  # ≈ 22 km de haut : quelques lieux par case en Europe
# Après un échec de téléchargement, on ne réessaie pas à chaque requête.
RETRY_AFTER_S = 60.0

# Quartiers (PPLX : les 16 quartiers de Marseille, les suburbs australiens…) et lieux
# disparus : la photo prise sur le Vieux-Port doit aller dans « Marseille ».
EXCLUDED_CODES = {"PPLX", "PPLH", "PPLQ", "PPLW", "PPLCH"}

# GeoNames nomme certaines régions françaises en anglais.
REGION_FR = {
    "FR.53": "Bretagne",
    "FR.94": "Corse",
    "FR.75": "Nouvelle-Aquitaine",
    "FR.28": "Normandie",
}

# … et quelques départements en anglais ou sans leurs traits d'union.
DEPT_FR = {
    "FR.94.2A": "Corse-du-Sud",
    "FR.94.2B": "Haute-Corse",
    "FR.76.31": "Haute-Garonne",
    "FR.27.21": "Côte-d'Or",
    "FR.11.95": "Val-d'Oise",
    "FR.44.54": "Meurthe-et-Moselle",
    "FR.24.41": "Loir-et-Cher",
}

# Arrondissements de Paris, Lyon et Marseille (« Paris 04 Hôtel-de-Ville »,
# « Marseille 02 ») : pour ranger des photos, c'est la ville qui compte.
ARRONDISSEMENT_RE = re.compile(r"^(Paris|Lyon|Marseille) \d{2}\b.*$")

DEPT_PREFIX_RE = re.compile(
    r"^D[ée]partement\s+(?:de\s+la\s+|de\s+l['’]\s*|des\s+|du\s+|de\s+|d['’]\s*)",
    re.IGNORECASE,
)

# ISO 3166-1 alpha-2 → nom français, plus XK (Kosovo, utilisé par GeoNames) et deux
# codes retirés encore présents dans de vieilles données (AN, CS).
COUNTRY_FR = {
    "AD": "Andorre", "AE": "Émirats arabes unis", "AF": "Afghanistan",
    "AG": "Antigua-et-Barbuda", "AI": "Anguilla", "AL": "Albanie", "AM": "Arménie",
    "AO": "Angola", "AQ": "Antarctique", "AR": "Argentine", "AS": "Samoa américaines",
    "AT": "Autriche", "AU": "Australie", "AW": "Aruba", "AX": "Îles Åland",
    "AZ": "Azerbaïdjan",
    "BA": "Bosnie-Herzégovine", "BB": "Barbade", "BD": "Bangladesh", "BE": "Belgique",
    "BF": "Burkina Faso", "BG": "Bulgarie", "BH": "Bahreïn", "BI": "Burundi",
    "BJ": "Bénin", "BL": "Saint-Barthélemy", "BM": "Bermudes", "BN": "Brunei",
    "BO": "Bolivie", "BQ": "Pays-Bas caribéens", "BR": "Brésil", "BS": "Bahamas",
    "BT": "Bhoutan", "BV": "Île Bouvet", "BW": "Botswana", "BY": "Biélorussie",
    "BZ": "Belize",
    "CA": "Canada", "CC": "Îles Cocos", "CD": "République démocratique du Congo",
    "CF": "République centrafricaine", "CG": "Congo", "CH": "Suisse",
    "CI": "Côte d'Ivoire", "CK": "Îles Cook", "CL": "Chili", "CM": "Cameroun",
    "CN": "Chine", "CO": "Colombie", "CR": "Costa Rica", "CU": "Cuba",
    "CV": "Cap-Vert", "CW": "Curaçao", "CX": "Île Christmas", "CY": "Chypre",
    "CZ": "Tchéquie",
    "DE": "Allemagne", "DJ": "Djibouti", "DK": "Danemark", "DM": "Dominique",
    "DO": "République dominicaine", "DZ": "Algérie",
    "EC": "Équateur", "EE": "Estonie", "EG": "Égypte", "EH": "Sahara occidental",
    "ER": "Érythrée", "ES": "Espagne", "ET": "Éthiopie",
    "FI": "Finlande", "FJ": "Fidji", "FK": "Îles Malouines", "FM": "Micronésie",
    "FO": "Îles Féroé", "FR": "France",
    "GA": "Gabon", "GB": "Royaume-Uni", "GD": "Grenade", "GE": "Géorgie",
    "GF": "Guyane", "GG": "Guernesey", "GH": "Ghana", "GI": "Gibraltar",
    "GL": "Groenland", "GM": "Gambie", "GN": "Guinée", "GP": "Guadeloupe",
    "GQ": "Guinée équatoriale", "GR": "Grèce",
    "GS": "Géorgie du Sud-et-les îles Sandwich du Sud", "GT": "Guatemala", "GU": "Guam",
    "GW": "Guinée-Bissau", "GY": "Guyana",
    "HK": "Hong Kong", "HM": "Îles Heard-et-MacDonald", "HN": "Honduras",
    "HR": "Croatie", "HT": "Haïti", "HU": "Hongrie",
    "ID": "Indonésie", "IE": "Irlande", "IL": "Israël", "IM": "Île de Man",
    "IN": "Inde", "IO": "Territoire britannique de l'océan Indien", "IQ": "Irak",
    "IR": "Iran", "IS": "Islande", "IT": "Italie",
    "JE": "Jersey", "JM": "Jamaïque", "JO": "Jordanie", "JP": "Japon",
    "KE": "Kenya", "KG": "Kirghizistan", "KH": "Cambodge", "KI": "Kiribati",
    "KM": "Comores", "KN": "Saint-Christophe-et-Niévès", "KP": "Corée du Nord",
    "KR": "Corée du Sud", "KW": "Koweït", "KY": "Îles Caïmans", "KZ": "Kazakhstan",
    "LA": "Laos", "LB": "Liban", "LC": "Sainte-Lucie", "LI": "Liechtenstein",
    "LK": "Sri Lanka", "LR": "Liberia", "LS": "Lesotho", "LT": "Lituanie",
    "LU": "Luxembourg", "LV": "Lettonie", "LY": "Libye",
    "MA": "Maroc", "MC": "Monaco", "MD": "Moldavie", "ME": "Monténégro",
    "MF": "Saint-Martin", "MG": "Madagascar", "MH": "Îles Marshall",
    "MK": "Macédoine du Nord", "ML": "Mali", "MM": "Birmanie", "MN": "Mongolie",
    "MO": "Macao", "MP": "Îles Mariannes du Nord", "MQ": "Martinique",
    "MR": "Mauritanie", "MS": "Montserrat", "MT": "Malte", "MU": "Maurice",
    "MV": "Maldives", "MW": "Malawi", "MX": "Mexique", "MY": "Malaisie",
    "MZ": "Mozambique",
    "NA": "Namibie", "NC": "Nouvelle-Calédonie", "NE": "Niger", "NF": "Île Norfolk",
    "NG": "Nigeria", "NI": "Nicaragua", "NL": "Pays-Bas", "NO": "Norvège",
    "NP": "Népal", "NR": "Nauru", "NU": "Niue", "NZ": "Nouvelle-Zélande",
    "OM": "Oman",
    "PA": "Panama", "PE": "Pérou", "PF": "Polynésie française",
    "PG": "Papouasie-Nouvelle-Guinée", "PH": "Philippines", "PK": "Pakistan",
    "PL": "Pologne", "PM": "Saint-Pierre-et-Miquelon", "PN": "Îles Pitcairn",
    "PR": "Porto Rico", "PS": "Palestine", "PT": "Portugal", "PW": "Palaos",
    "PY": "Paraguay",
    "QA": "Qatar",
    "RE": "La Réunion", "RO": "Roumanie", "RS": "Serbie", "RU": "Russie",
    "RW": "Rwanda",
    "SA": "Arabie saoudite", "SB": "Îles Salomon", "SC": "Seychelles", "SD": "Soudan",
    "SE": "Suède", "SG": "Singapour", "SH": "Sainte-Hélène", "SI": "Slovénie",
    "SJ": "Svalbard et Jan Mayen", "SK": "Slovaquie", "SL": "Sierra Leone",
    "SM": "Saint-Marin", "SN": "Sénégal", "SO": "Somalie", "SR": "Suriname",
    "SS": "Soudan du Sud", "ST": "Sao Tomé-et-Principe", "SV": "Salvador",
    "SX": "Saint-Martin (partie néerlandaise)", "SY": "Syrie", "SZ": "Eswatini",
    "TC": "Îles Turques-et-Caïques", "TD": "Tchad",
    "TF": "Terres australes et antarctiques françaises", "TG": "Togo",
    "TH": "Thaïlande", "TJ": "Tadjikistan", "TK": "Tokelau", "TL": "Timor oriental",
    "TM": "Turkménistan", "TN": "Tunisie", "TO": "Tonga", "TR": "Turquie",
    "TT": "Trinité-et-Tobago", "TV": "Tuvalu", "TW": "Taïwan", "TZ": "Tanzanie",
    "UA": "Ukraine", "UG": "Ouganda", "UM": "Îles mineures éloignées des États-Unis",
    "US": "États-Unis", "UY": "Uruguay", "UZ": "Ouzbékistan",
    "VA": "Vatican", "VC": "Saint-Vincent-et-les-Grenadines", "VE": "Venezuela",
    "VG": "Îles Vierges britanniques", "VI": "Îles Vierges des États-Unis",
    "VN": "Viêt Nam", "VU": "Vanuatu",
    "WF": "Wallis-et-Futuna", "WS": "Samoa",
    "YE": "Yémen", "YT": "Mayotte",
    "ZA": "Afrique du Sud", "ZM": "Zambie", "ZW": "Zimbabwe",
    "XK": "Kosovo", "AN": "Antilles néerlandaises", "CS": "Serbie-et-Monténégro",
}


class GeoUnavailable(Exception):
    """Données GeoNames absentes ou illisibles ; message affichable tel quel."""


def clean_city(name: str, cc: str) -> str:
    if cc == "FR":
        return ARRONDISSEMENT_RE.sub(r"\1", name)
    return name


def clean_departement(name: str) -> str:
    """« Département du Vaucluse » → « Vaucluse » (GeoNames en garde quelques-uns)."""
    return DEPT_PREFIX_RE.sub("", name, count=1).strip() or name


def parse_point(raw: Any) -> tuple[float, float] | None:
    """`[lat, lon]` valide → tuple ; tout le reste (chaîne, booléen, NaN, hors bornes) → None."""
    if not isinstance(raw, (list, tuple)) or len(raw) != 2:
        return None
    lat, lon = raw
    if isinstance(lat, bool) or isinstance(lon, bool):
        return None
    if not isinstance(lat, (int, float)) or not isinstance(lon, (int, float)):
        return None
    lat, lon = float(lat), float(lon)
    if not (math.isfinite(lat) and math.isfinite(lon)):
        return None
    if not (-90.0 <= lat <= 90.0 and -180.0 <= lon <= 180.0):
        return None
    return lat, lon


class GeoIndex:
    """Lieux habités en mémoire, indexés par une grille de CELL_DEG degrés.

    `places` : itérable de (lat, lon, nom, code pays, code admin1, code admin2).
    `admin1`/`admin2` : « FR.84 » → nom, « FR.84.69 » → nom.
    """

    def __init__(self, places: Iterable[tuple], admin1: dict[str, str],
                 admin2: dict[str, str], cell_deg: float = CELL_DEG):
        self.cell = cell_deg
        self.ncols = round(360 / cell_deg)
        self.nrows = round(180 / cell_deg)
        names: dict[str, int] = {}
        name_list: list[str] = []
        admins: dict[tuple, int] = {}
        admin_list: list[dict] = []
        keys, lats, lons = array("q"), array("d"), array("d")
        name_ids, admin_ids = array("I"), array("I")
        for lat, lon, name, cc, a1, a2 in places:
            name = clean_city(name, cc)
            ni = names.get(name)
            if ni is None:
                ni = names[name] = len(name_list)
                name_list.append(name)
            akey = (cc, a1, a2)
            ai = admins.get(akey)
            if ai is None:
                ai = admins[akey] = len(admin_list)
                admin_list.append(self._admin(cc, a1, a2, admin1, admin2))
            keys.append(self._key(lat, lon))
            lats.append(lat)
            lons.append(lon)
            name_ids.append(ni)
            admin_ids.append(ai)
        # Tri par case : les lieux d'une case deviennent une tranche contiguë.
        order = sorted(range(len(keys)), key=keys.__getitem__)
        self.lat = array("d", (lats[i] for i in order))
        self.lon = array("d", (lons[i] for i in order))
        self.name_id = array("I", (name_ids[i] for i in order))
        self.admin_id = array("I", (admin_ids[i] for i in order))
        keys = array("q", (keys[i] for i in order))
        del order, lats, lons, name_ids, admin_ids
        # case → début * 2^16 + nombre : un seul int par case.
        cells: dict[int, int] = {}
        start = 0
        for i in range(1, len(keys) + 1):
            if i == len(keys) or keys[i] != keys[start]:
                cells[keys[start]] = (start << 16) | (i - start)
                start = i
        self.cells = cells
        self.names = name_list
        self.admins = admin_list

    @staticmethod
    def _admin(cc: str, a1: str, a2: str, admin1: dict, admin2: dict) -> dict:
        code1, code2 = f"{cc}.{a1}", f"{cc}.{a1}.{a2}"
        region = REGION_FR.get(code1) or admin1.get(code1)
        dept = DEPT_FR.get(code2) or (admin2.get(code2) if a2 else None)
        return {
            "departement": clean_departement(dept) if dept else None,
            "region": region or None,
            "pays": COUNTRY_FR.get(cc, cc) or None,
            "codePays": cc or None,
        }

    def __len__(self) -> int:
        return len(self.lat)

    def _row(self, lat: float) -> int:
        return min(self.nrows - 1, max(0, int((lat + 90.0) // self.cell)))

    def _col(self, lon: float) -> int:
        return int((lon + 180.0) // self.cell) % self.ncols

    def _key(self, lat: float, lon: float) -> int:
        return self._row(lat) * self.ncols + self._col(lon)

    def _scan(self, keys, lat, lon, coslat, best, best_d2):
        lats, lons, cells = self.lat, self.lon, self.cells
        for key in keys:
            packed = cells.get(key)
            if packed is None:
                continue
            start = packed >> 16
            for k in range(start, start + (packed & 0xFFFF)):
                dlon = lons[k] - lon
                if dlon > 180.0:
                    dlon -= 360.0
                elif dlon < -180.0:
                    dlon += 360.0
                dlat = lats[k] - lat
                dx = dlon * coslat
                d2 = dlat * dlat + dx * dx
                if d2 < best_d2:
                    best, best_d2 = k, d2
        return best, best_d2

    def nearest(self, lat: float, lon: float, max_km: float = COUNTRY_KM) -> tuple[int, float] | None:
        """Indice du lieu le plus proche à moins de `max_km`, et sa distance en km.

        Distance équirectangulaire (cos de la latitude du point) : < 1 % d'écart avec
        la haversine à 100 km, et assez rapide en Python pur pour 5 000 points.
        """
        cell = self.cell
        coslat = max(math.cos(math.radians(lat)), 0.01)
        max_deg = max_km / KM_PER_DEG
        r0, c0 = self._row(lat), self._col(lon)
        ncols = self.ncols
        # 1) Les 3×3 cases autour du point : suffit presque toujours sur terre.
        first = set()
        for r in range(r0 - 1, r0 + 2):
            if 0 <= r < self.nrows:
                for c in range(c0 - 1, c0 + 2):
                    first.add(r * ncols + c % ncols)
        best, best_d2 = self._scan(first, lat, lon, coslat, -1, max_deg * max_deg)
        # Distance garantie couverte par ces cases (bords du carré 3×3).
        lat_lo, lat_hi = (r0 - 1) * cell - 90.0, (r0 + 2) * cell - 90.0
        lon_lo = (int((lon + 180.0) // cell) - 1) * cell - 180.0
        lon_hi = lon_lo + 3 * cell
        covered = min(lat - lat_lo, lat_hi - lat, (lon - lon_lo) * coslat, (lon_hi - lon) * coslat)
        reach = math.sqrt(best_d2)  # = max_deg si rien trouvé
        if reach > covered:
            # 2) Le carré exact qui peut contenir mieux : rayon = meilleur trouvé, ou max_km.
            dlon = reach / coslat
            rows = range(self._row(lat - reach), self._row(lat + reach) + 1)
            c_lo = int((lon - dlon + 180.0) // cell)
            c_hi = int((lon + dlon + 180.0) // cell)
            cols = range(ncols) if c_hi - c_lo + 1 >= ncols else range(c_lo, c_hi + 1)
            keys = (r * ncols + c % ncols for r in rows for c in cols)
            best, best_d2 = self._scan(
                (k for k in keys if k not in first), lat, lon, coslat, best, best_d2)
        if best < 0:
            return None
        return best, math.sqrt(best_d2) * KM_PER_DEG

    def lookup(self, lat: float, lon: float) -> dict | None:
        found = self.nearest(lat, lon, COUNTRY_KM)
        if found is None:
            return None
        k, km = found
        return {"ville": self.names[self.name_id[k]] if km <= CITY_KM else None,
                **self.admins[self.admin_id[k]]}

    def reverse(self, raw_points: list) -> list[dict | None]:
        # Les photos d'une même sortie partagent souvent leur position à quelques
        # mètres près : on ne cherche qu'une fois par point arrondi (~10 m).
        memo: dict[tuple[float, float], dict | None] = {}
        out: list[dict | None] = []
        for raw in raw_points:
            point = parse_point(raw)
            if point is None:
                out.append(None)
                continue
            rounded = (round(point[0], 4), round(point[1], 4))
            if rounded not in memo:
                memo[rounded] = self.lookup(*point)
            out.append(memo[rounded])
        return out


def read_admin(text: Iterable[str]) -> dict[str, str]:
    """`admin1CodesASCII.txt` / `admin2Codes.txt` : code<TAB>nom<TAB>nom ascii<TAB>id."""
    names = {}
    for line in text:
        parts = line.rstrip("\n").split("\t")
        if len(parts) >= 2 and parts[0] and parts[1]:
            names[parts[0]] = parts[1]
    return names


def read_cities(text: Iterable[str]):
    """Lignes de `cities500.txt` → (lat, lon, nom, pays, admin1, admin2), lieux habités seuls."""
    intern = sys.intern
    for line in text:
        f = line.split("\t")
        if len(f) < 12 or f[6] != "P" or f[7] in EXCLUDED_CODES:
            continue
        try:
            lat, lon = float(f[4]), float(f[5])
        except ValueError:
            continue
        yield lat, lon, f[1], intern(f[8]), intern(f[10]), intern(f[11])


def load_index(directory: Path) -> GeoIndex:
    with open(directory / ADMIN1_TXT, encoding="utf-8") as fh:
        admin1 = read_admin(fh)
    with open(directory / ADMIN2_TXT, encoding="utf-8") as fh:
        admin2 = read_admin(fh)
    with zipfile.ZipFile(directory / CITIES_ZIP) as zf:
        with zf.open(CITIES_TXT) as raw:
            text = io.TextIOWrapper(raw, encoding="utf-8")
            return GeoIndex(read_cities(text), admin1, admin2)


def http_fetch(url: str, dest: Path) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": "YayoExtractor/1.0"})
    with urllib.request.urlopen(request, timeout=60) as resp, open(dest, "wb") as out:
        while chunk := resp.read(1 << 20):
            out.write(chunk)


class Geocoder:
    """Télécharge (une fois, un seul à la fois), charge (une fois) et interroge l'index."""

    def __init__(self, directory: Path, fetch: Callable[[str, Path], None] = http_fetch,
                 base_url: str = GEONAMES_URL):
        self.directory = Path(directory)
        self.fetch = fetch
        self.base_url = base_url
        self._lock = threading.Lock()
        self._index: GeoIndex | None = None
        self._failed_at = 0.0

    def files_present(self) -> bool:
        return all((self.directory / name).is_file() for name in FILES)

    def _download_missing(self) -> None:
        self.directory.mkdir(parents=True, exist_ok=True)
        for name in FILES:
            final = self.directory / name
            if final.is_file():
                continue
            # Fichier temporaire puis renommage : un téléchargement coupé ne laisse
            # jamais un fichier tronqué qu'on prendrait pour bon au prochain démarrage.
            part = self.directory / f"{name}.{uuid.uuid4().hex}.part"
            try:
                self.fetch(self.base_url + name, part)
                if part.stat().st_size == 0:
                    raise OSError(f"{name} vide")
                if name.endswith(".zip"):
                    with zipfile.ZipFile(part) as zf:
                        if CITIES_TXT not in zf.namelist():
                            raise OSError(f"{CITIES_TXT} absent de {name}")
                os.replace(part, final)
                log.info("géocodage : %s téléchargé (%d Ko)", name, final.stat().st_size // 1024)
            finally:
                part.unlink(missing_ok=True)

    def index(self) -> GeoIndex:
        """L'index chargé ; télécharge et charge au premier appel. Lève GeoUnavailable."""
        if self._index is not None:
            return self._index
        with self._lock:
            if self._index is not None:
                return self._index
            if not self.files_present():
                if time.monotonic() - self._failed_at < RETRY_AFTER_S:
                    raise GeoUnavailable(
                        "Les données des lieux ne sont pas encore prêtes, réessaie dans une minute.")
                try:
                    self._download_missing()
                except Exception as exc:
                    self._failed_at = time.monotonic()
                    log.warning("géocodage : téléchargement GeoNames impossible (%s)", exc)
                    raise GeoUnavailable(
                        "Les données des lieux n'ont pas pu être téléchargées, réessaie plus tard.")
            started = time.monotonic()
            try:
                self._index = load_index(self.directory)
            except Exception as exc:
                log.warning("géocodage : données GeoNames illisibles (%s)", exc)
                # Fichiers corrompus : on les retire pour qu'un prochain essai les retélécharge.
                for name in FILES:
                    (self.directory / name).unlink(missing_ok=True)
                self._failed_at = time.monotonic()
                raise GeoUnavailable("Les données des lieux sont abîmées, réessaie plus tard.")
            log.info("géocodage : %d lieux chargés en %.1f s",
                     len(self._index), time.monotonic() - started)
            return self._index

    def prefetch(self) -> None:
        """Au démarrage, en tâche de fond : télécharge les fichiers manquants sans les
        charger (≈ 50 Mo de mémoire qu'on n'occupe que si le tri par lieu sert)."""
        if self.files_present():
            return
        with self._lock:
            try:
                self._download_missing()
            except Exception as exc:
                self._failed_at = time.monotonic()
                log.warning("géocodage : téléchargement GeoNames impossible (%s)", exc)

    def reverse(self, points: list) -> list[dict | None]:
        return self.index().reverse(points)
