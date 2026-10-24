"""Géocodage inverse : jeu GeoNames synthétique, aucun accès réseau."""

import math
import shutil
import threading
import zipfile

import pytest
from fastapi.testclient import TestClient

from app import geo
from app import main

KM = geo.KM_PER_DEG  # km par degré de latitude


def city(name, lat, lon, cc="FR", a1="84", a2="69", fclass="P", fcode="PPL"):
    """Une ligne de cities500.txt (19 colonnes)."""
    cols = ["1", name, name, "", str(lat), str(lon), fclass, fcode, cc, "", a1, a2,
            "", "", "1000", "", "200", "Europe/Paris", "2026-01-01"]
    return "\t".join(cols)


CITIES = [
    city("Lyon", 45.764, 4.8357),
    city("Villeurbanne", 45.7667, 4.8803),
    city("Lyon 03", 45.76, 4.85, fcode="PPLX"),  # quartier : ignoré
    city("Marseille 02", 43.30, 5.37, a1="93", a2="13", fcode="PPLA5"),
    city("Vaison", 44.24, 5.07, a1="93", a2="84"),
    city("Ajaccio", 41.92, 8.74, a1="94", a2="2A"),
    city("Tahoua", 14.89, 5.26, cc="NE", a1="06", a2="2439375"),
    city("Mont Blanc", 45.83, 6.86, fclass="T", fcode="MT"),  # pas un lieu habité
    city("Isolée", 10.0, 20.0, cc="TD", a1="01", a2=""),
    city("Levuka Est", -16.8, 179.95, cc="FJ", a1="01", a2=""),
]
ADMIN1 = [
    "FR.84\tAuvergne-Rhône-Alpes\tAuvergne-Rhone-Alpes\t1",
    "FR.93\tProvence-Alpes-Côte d'Azur\tProvence-Alpes-Cote d'Azur\t2",
    "FR.94\tCorsica\tCorsica\t3",
    "NE.06\tTahoua\tTahoua\t4",
]
ADMIN2 = [
    "FR.84.69\tRhône\tRhone\t1",
    "FR.93.13\tBouches-du-Rhône\tBouches-du-Rhone\t2",
    "FR.93.84\tDépartement du Vaucluse\tDepartement du Vaucluse\t3",
    "FR.94.2A\tSouth Corsica\tSouth Corsica\t4",
    "NE.06.2439375\tDépartement de Tahoua\tDepartement de Tahoua\t5",
]


def write_geonames(directory, cities=CITIES):
    directory.mkdir(parents=True, exist_ok=True)
    (directory / geo.ADMIN1_TXT).write_text("\n".join(ADMIN1) + "\n", encoding="utf-8")
    (directory / geo.ADMIN2_TXT).write_text("\n".join(ADMIN2) + "\n", encoding="utf-8")
    with zipfile.ZipFile(directory / geo.CITIES_ZIP, "w") as zf:
        zf.writestr(geo.CITIES_TXT, "\n".join(cities) + "\n")
    return directory


@pytest.fixture
def index(tmp_path):
    return geo.load_index(write_geonames(tmp_path / "geonames"))


def no_network(url, dest):
    raise AssertionError(f"accès réseau inattendu : {url}")


@pytest.fixture
def client(tmp_path, monkeypatch):
    coder = geo.Geocoder(write_geonames(tmp_path / "geonames"), fetch=no_network)
    monkeypatch.setattr(main, "geocoder", coder)
    return TestClient(main.app)  # sans `with` : pas de lifespan, donc pas de prefetch


# ---------- Plus proche voisin et seuils ----------


def test_plus_proche_voisin(index):
    assert index.lookup(45.764, 4.84)["ville"] == "Lyon"
    assert index.lookup(45.767, 4.875)["ville"] == "Villeurbanne"


def test_lieux_non_habites_et_quartiers_ignores(index):
    assert len(index) == len(CITIES) - 2
    # Le quartier « Lyon 03 » est exactement là, mais c'est Lyon qui doit sortir.
    assert index.lookup(45.76, 4.85)["ville"] == "Lyon"
    # Le Mont Blanc (classe T) n'existe pas pour l'index : rien à moins de 100 km.
    assert index.lookup(45.83, 6.86) is None


def test_seuil_ville_30_km(index):
    near = index.lookup(10.0 + 29 / KM, 20.0)
    assert near == {"ville": "Isolée", "departement": None, "region": None,
                    "pays": "Tchad", "codePays": "TD"}
    far = index.lookup(10.0 + 31 / KM, 20.0)
    assert far["ville"] is None and far["pays"] == "Tchad"


def test_seuil_pays_100_km(index):
    assert index.lookup(10.0 + 99 / KM, 20.0)["codePays"] == "TD"
    assert index.lookup(10.0 + 101 / KM, 20.0) is None
    # Même chose en longitude, où un degré est plus court (cos 10°).
    deg = 99 / (KM * math.cos(math.radians(10.0)))
    assert index.lookup(10.0, 20.0 + deg)["codePays"] == "TD"


def test_pleine_mer(index):
    assert index.lookup(40.0, -40.0) is None


def test_antimeridien(index):
    place = index.lookup(-16.8, -179.95)  # ≈ 10 km à l'est de Levuka Est, de l'autre côté
    assert place["ville"] == "Levuka Est" and place["pays"] == "Fidji"


def test_noms_en_francais(index):
    assert index.lookup(45.764, 4.8357) == {
        "ville": "Lyon", "departement": "Rhône", "region": "Auvergne-Rhône-Alpes",
        "pays": "France", "codePays": "FR"}
    corse = index.lookup(41.92, 8.74)
    assert (corse["departement"], corse["region"]) == ("Corse-du-Sud", "Corse")
    assert index.lookup(43.30, 5.37)["ville"] == "Marseille"  # arrondissement → ville
    assert index.lookup(14.89, 5.26)["pays"] == "Niger"


def test_table_des_pays_complete():
    assert len(geo.COUNTRY_FR) >= 250
    assert all(len(code) == 2 and code.isupper() for code in geo.COUNTRY_FR)
    assert geo.COUNTRY_FR["US"] == "États-Unis" and geo.COUNTRY_FR["GB"] == "Royaume-Uni"


# ---------- Départements ----------


@pytest.mark.parametrize("raw, clean", [
    ("Département du Vaucluse", "Vaucluse"),
    ("Département du Nord", "Nord"),
    ("Département de la Lékié", "Lékié"),
    ("Département de l'Océan", "Océan"),
    ("Département de l’Océan", "Océan"),
    ("Département des Landes", "Landes"),
    ("Département de Tahoua", "Tahoua"),
    ("Département d' Illéla", "Illéla"),
    ("Département d’ Aguié", "Aguié"),
    ("Departement du Haut-Nyong", "Haut-Nyong"),
    ("Département de Dosso", "Dosso"),
    ("Rhône", "Rhône"),
    ("Deux-Sèvres", "Deux-Sèvres"),
    ("Département", "Département"),
])
def test_prefixes_de_departement(raw, clean):
    assert geo.clean_departement(raw) == clean


def test_departement_nettoye_dans_la_reponse(index):
    assert index.lookup(44.24, 5.07)["departement"] == "Vaucluse"
    assert index.lookup(14.89, 5.26)["departement"] == "Tahoua"


# ---------- Validation des points ----------


@pytest.mark.parametrize("raw", [
    None, "48.8,2.3", [48.8], [48.8, 2.3, 0], [True, 2.3], ["48.8", "2.3"],
    [91, 0], [0, -180.5], [float("nan"), 0], [0, float("inf")], {"lat": 1, "lon": 2},
])
def test_points_invalides(raw):
    assert geo.parse_point(raw) is None


def test_points_valides():
    assert geo.parse_point([45, 5]) == (45.0, 5.0)
    assert geo.parse_point((-90, 180)) == (-90.0, 180.0)


# ---------- Route ----------


def test_route_ordre_et_null(client):
    points = [[45.764, 4.8357], "nimporte", [40.0, -40.0], [91, 0],
              [41.92, 8.74], None, [45.764, 4.8357]]
    resp = client.post("/api/geo/reverse", json={"points": points})
    assert resp.status_code == 200
    places = resp.json()["places"]
    assert len(places) == len(points)
    assert [p and p["ville"] for p in places] == [
        "Lyon", None, None, None, "Ajaccio", None, "Lyon"]


def test_route_limite_5000(client):
    ok = client.post("/api/geo/reverse", json={"points": [[45.764, 4.8357]] * 5000})
    assert ok.status_code == 200 and len(ok.json()["places"]) == 5000
    trop = client.post("/api/geo/reverse", json={"points": [[45.764, 4.8357]] * 5001})
    assert trop.status_code == 400
    assert "5000" in trop.json()["detail"]


def test_route_corps_invalide(client):
    assert client.post("/api/geo/reverse", json={"pts": []}).status_code == 422
    assert client.post("/api/geo/reverse", json={"points": "x"}).status_code == 422
    assert client.post("/api/geo/reverse", json={"points": []}).json() == {"places": []}


def test_route_503_si_donnees_indisponibles(tmp_path, monkeypatch):
    calls = []

    def down(url, dest):
        calls.append(url)
        raise OSError("réseau coupé")

    monkeypatch.setattr(main, "geocoder", geo.Geocoder(tmp_path / "vide", fetch=down))
    client = TestClient(main.app)
    resp = client.post("/api/geo/reverse", json={"points": [[45, 5]]})
    assert resp.status_code == 503
    assert "réessaie" in resp.json()["detail"]
    # Pas de nouvel essai à chaque requête juste après un échec.
    assert client.post("/api/geo/reverse", json={"points": [[45, 5]]}).status_code == 503
    assert len(calls) == 1
    assert not list((tmp_path / "vide").glob("*.part"))


# ---------- Téléchargement ----------


def copying_fetch(source, calls):
    def fetch(url, dest):
        calls.append(url)
        shutil.copyfile(source / url.rsplit("/", 1)[1], dest)
    return fetch


def test_telechargement_au_premier_besoin(tmp_path):
    source = write_geonames(tmp_path / "source")
    calls = []
    coder = geo.Geocoder(tmp_path / "data" / "geonames", fetch=copying_fetch(source, calls))
    assert coder.reverse([[45.764, 4.8357]])[0]["ville"] == "Lyon"
    assert sorted(c.rsplit("/", 1)[1] for c in calls) == sorted(geo.FILES)
    assert sorted(p.name for p in coder.directory.iterdir()) == sorted(geo.FILES)
    coder.reverse([[45.764, 4.8357]])
    assert len(calls) == 3  # chargé une fois, plus de téléchargement


def test_un_seul_telechargement_a_la_fois(tmp_path):
    source = write_geonames(tmp_path / "source")
    calls = []
    gate = threading.Event()
    inner = copying_fetch(source, calls)

    def slow(url, dest):
        gate.wait(5)
        inner(url, dest)

    coder = geo.Geocoder(tmp_path / "geonames", fetch=slow)
    results = []
    threads = [threading.Thread(target=lambda: results.append(coder.reverse([[45.764, 4.8357]])))
               for _ in range(4)]
    for t in threads:
        t.start()
    gate.set()
    for t in threads:
        t.join(10)
    assert len(results) == 4 and len(calls) == 3


def test_echec_partiel_puis_reessai(tmp_path, monkeypatch):
    source = write_geonames(tmp_path / "source")
    calls = []
    good = copying_fetch(source, calls)
    broken = []

    def flaky(url, dest):
        if url.endswith(geo.ADMIN2_TXT) and not broken:
            broken.append(url)
            dest.write_bytes(b"FR.84.69\tRh")  # coupé en plein vol
            raise OSError("connexion perdue")
        good(url, dest)

    coder = geo.Geocoder(tmp_path / "geonames", fetch=flaky)
    with pytest.raises(geo.GeoUnavailable):
        coder.index()
    assert not (coder.directory / geo.ADMIN2_TXT).exists()  # jamais de fichier tronqué
    assert not list(coder.directory.glob("*.part"))
    # Plus tard, on réessaie : seul le fichier manquant est retéléchargé.
    monkeypatch.setattr(geo, "RETRY_AFTER_S", 0)
    assert coder.reverse([[45.764, 4.8357]])[0]["ville"] == "Lyon"
    assert sorted(c.rsplit("/", 1)[1] for c in calls) == sorted(geo.FILES)


def test_zip_invalide_refuse(tmp_path):
    def bad_zip(url, dest):
        dest.write_bytes(b"pas un zip" if url.endswith(".zip") else b"x\ty\n")

    coder = geo.Geocoder(tmp_path / "geonames", fetch=bad_zip)
    with pytest.raises(geo.GeoUnavailable):
        coder.index()
    assert not (coder.directory / geo.CITIES_ZIP).exists()


def test_donnees_abimees_retirees(tmp_path):
    directory = write_geonames(tmp_path / "geonames")
    (directory / geo.CITIES_ZIP).write_bytes(b"abime")
    coder = geo.Geocoder(directory, fetch=no_network)
    with pytest.raises(geo.GeoUnavailable):
        coder.index()
    assert not (directory / geo.CITIES_ZIP).exists()  # retéléchargé au prochain essai


def test_prefetch_ne_charge_pas(tmp_path):
    source = write_geonames(tmp_path / "source")
    calls = []
    coder = geo.Geocoder(tmp_path / "geonames", fetch=copying_fetch(source, calls))
    coder.prefetch()
    assert coder.files_present() and coder._index is None
    coder.prefetch()
    assert len(calls) == 3


def test_prefetch_echec_silencieux(tmp_path):
    def down(url, dest):
        raise OSError("dns")

    coder = geo.Geocoder(tmp_path / "geonames", fetch=down)
    coder.prefetch()  # ne lève rien : le démarrage continue
    assert not coder.files_present()
