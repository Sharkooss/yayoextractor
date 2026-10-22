"""Réparation des photos que les logiciels de montage refusent d'importer.

Cas d'origine (21/10/2026) : les photos reçues par WhatsApp sont des JPEG
*progressifs* (marqueur SOF2 au lieu de SOF0). Les navigateurs et les
visionneuses les affichent sans broncher, mais beaucoup de logiciels de montage
grand public ne savent décoder que le JPEG « baseline » et refusent le fichier.

On ré-encode donc tout en JPEG baseline 8 bits, RVB, sRGB, sans métadonnées :
le plus petit dénominateur commun que tous les logiciels lisent. Au passage on
neutralise les autres causes de refus connues (HEIC d'iPhone, rotation EXIF
ignorée, couleurs CMJN, transparence, résolutions géantes, fichier tronqué).
"""

import io
import re
import unicodedata
from typing import BinaryIO

from PIL import Image, ImageCms, ImageFile, ImageOps, JpegImagePlugin, UnidentifiedImageError
from pillow_heif import register_heif_opener

register_heif_opener()  # ouvre les HEIC/HEIF des iPhone
# Un JPEG tronqué (envoi interrompu) est récupéré jusqu'où il va, le reste en gris,
# plutôt que refusé : c'est justement le genre de fichier qu'on nous apportera.
ImageFile.LOAD_TRUNCATED_IMAGES = True

# Au-delà, certains logiciels de montage plantent ou refusent l'image ; 4096 px
# couvrent largement une vidéo 4K (3840 px).
MAX_SIDE = 4096
# Qualité quand on ne peut pas reprendre les tables de la photo d'origine.
FALLBACK_QUALITY = 93
SRGB = ImageCms.createProfile("sRGB")

# Libellés affichés à l'utilisateur, par code d'anomalie.
ISSUE_LABELS = {
    "progressive": "JPEG progressif : illisible pour beaucoup de logiciels de montage",
    "format": "Format non standard, converti en JPEG",
    "mpo": "Photo multi-images (MPO), seule l'image principale est gardée",
    "truncated": "Fichier incomplet, récupéré au maximum",
    "orientation": "Rotation cachée dans les métadonnées, appliquée pour de vrai",
    "cmyk": "Couleurs d'imprimerie (CMJN), converties pour l'écran",
    "alpha": "Transparence remplacée par un fond blanc",
    "depth": "Couleurs 16 bits, ramenées à 8 bits",
    "grayscale": "Noir et blanc, enregistré en couleurs standard",
    "color_profile": "Profil de couleurs exotique, converti en sRGB",
    "huge": f"Résolution géante, réduite à {MAX_SIDE} px",
}


class PhotoError(Exception):
    """Photo irrécupérable ; message affichable tel quel."""


def output_name(original: str) -> str:
    """Nom ASCII sans espaces : de vieux logiciels butent sur les accents."""
    stem = original.rsplit(".", 1)[0] if "." in original else original
    stem = unicodedata.normalize("NFKD", stem).encode("ascii", "ignore").decode()
    stem = re.sub(r"[^A-Za-z0-9_-]+", "-", stem).strip("-_")[:80]
    return f"{stem or 'photo'}-corrigee.jpg"


def _is_truncated_jpeg(src: BinaryIO) -> bool:
    src.seek(0, io.SEEK_END)
    size = src.tell()
    src.seek(max(0, size - 64))
    tail = src.read().rstrip(b"\x00")
    src.seek(0)
    return not tail.endswith(b"\xff\xd9")


def _to_srgb(img: Image.Image, issues: list[str]) -> Image.Image:
    if img.mode in ("I;16", "I;16B", "I;16L", "I", "F"):
        issues.append("depth")
        img = img.convert("I").point(lambda v: v * (1 / 256)).convert("L")

    if img.mode == "P":
        img = img.convert("RGBA" if "transparency" in img.info else "RGB")
    if img.mode in ("RGBA", "LA", "PA", "RGBa", "La"):
        issues.append("alpha")
        rgba = img.convert("RGBA")
        flat = Image.new("RGB", rgba.size, (255, 255, 255))
        flat.paste(rgba, mask=rgba.getchannel("A"))
        flat.info = img.info
        img = flat
    if img.mode == "CMYK":
        issues.append("cmyk")
    elif img.mode in ("L", "1"):
        issues.append("grayscale")

    icc = img.info.get("icc_profile")
    if icc:
        try:
            profile = ImageCms.ImageCmsProfile(io.BytesIO(icc))
            name = (ImageCms.getProfileDescription(profile) or "").lower()
            cmyk = img.mode == "CMYK"
            if cmyk or "srgb" not in name:
                img = ImageCms.profileToProfile(img, profile, SRGB, outputMode="RGB")
                if not cmyk:  # le CMJN est déjà signalé
                    issues.append("color_profile")
        except (ImageCms.PyCMSError, OSError, ValueError):
            pass  # profil illisible : on se contente de la conversion brute
    return img.convert("RGB") if img.mode != "RGB" else img


def repair(src: BinaryIO) -> tuple[bytes, dict]:
    """Renvoie (JPEG baseline, diagnostic). Lève PhotoError si irrécupérable."""
    try:
        img = Image.open(src)
    except Image.DecompressionBombError:
        raise PhotoError("Cette image est bien trop grande pour être traitée.")
    except (UnidentifiedImageError, OSError):
        raise PhotoError("Ce fichier n'est pas une photo reconnue.")

    with img:
        fmt = img.format or "?"
        issues: list[str] = []
        if fmt in ("JPEG", "MPO"):
            if img.info.get("progressive") or img.info.get("progression"):
                issues.append("progressive")
            if fmt == "MPO":
                issues.append("mpo")
            if _is_truncated_jpeg(src):
                issues.append("truncated")
        else:
            issues.append("format")

        # Reprendre les tables de quantification d'origine garde la même qualité
        # (et à peu près le même poids) qu'avant, sans perte de génération visible.
        tables = None
        if fmt in ("JPEG", "MPO") and img.mode == "RGB":
            sampling = JpegImagePlugin.get_sampling(img)
            quant = getattr(img, "quantization", None) or {}
            if sampling != -1 and len(quant) >= 2:
                tables = {"qtables": quant, "subsampling": sampling}

        orig_size = img.size
        try:
            img.load()
            orientation = img.getexif().get(0x0112, 1)
            if orientation not in (None, 1):
                issues.append("orientation")
            img = ImageOps.exif_transpose(img)
            img = _to_srgb(img, issues)
        except Image.DecompressionBombError:
            raise PhotoError("Cette image est bien trop grande pour être traitée.")
        except (OSError, SyntaxError, ValueError):
            raise PhotoError("Cette photo est trop abîmée pour être réparée.")

        if max(img.size) > MAX_SIDE:
            issues.append("huge")
            img.thumbnail((MAX_SIDE, MAX_SIDE), Image.Resampling.LANCZOS)

        out = io.BytesIO()
        options = tables or {"quality": FALLBACK_QUALITY, "subsampling": "4:2:0"}
        img.save(out, format="JPEG", progressive=False, optimize=False, **options)

    return out.getvalue(), {
        "format": "HEIC" if fmt == "HEIF" else fmt,
        "issues": issues,
        "orig_width": orig_size[0],
        "orig_height": orig_size[1],
        "width": img.size[0],
        "height": img.size[1],
    }
