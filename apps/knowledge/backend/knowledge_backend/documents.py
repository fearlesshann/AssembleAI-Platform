"""Deterministic text extraction. Run untrusted files through the isolated worker."""
from dataclasses import dataclass
from io import BytesIO
import logging
from pathlib import PurePosixPath
import re
import xml.etree.ElementTree as ET
import zipfile

MAX_BYTES = 5 * 1024 * 1024
MAX_TEXT = 1_000_000
MAX_CHUNKS = 2000
CHUNK_SIZE = 1200
OVERLAP = 150
MAX_XML = 8 * 1024 * 1024
MAX_UNPACKED = 16 * 1024 * 1024
W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
EXCLUDED = {W + name for name in ("del", "moveFrom", "drawing", "pict", "object")}
CODES = frozenset({"invalid_document", "unsupported_document", "encrypted_document", "ocr_required",
                   "empty_document", "document_too_large", "extracted_text_too_large", "document_too_complex",
                   "parser_timeout", "parser_unavailable"})


class DocumentError(Exception):
    def __init__(self, code):
        self.code = code if code in CODES else "invalid_document"
        super().__init__(self.code)


@dataclass(frozen=True)
class DocumentChunk:
    index: int
    text: str
    locator: str


@dataclass(frozen=True)
class ParsedDocument:
    kind: str
    chunks: tuple[DocumentChunk, ...]


def document_kind(filename):
    if (not isinstance(filename, str) or not filename or len(filename) > 255 or filename != filename.strip()
            or filename.endswith(".") or any(ord(c) < 32 or c in '/\\:*?"<>|\x7f' for c in filename)
            or re.fullmatch(r"(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", filename)):
        raise DocumentError("invalid_document")
    kind = filename.rsplit(".", 1)[-1].lower()
    if "." not in filename or kind not in {"pdf", "docx", "txt", "md"}:
        raise DocumentError("unsupported_document")
    return kind


def _clean_text(text):
    if any((ord(char) < 32 and char not in "\t\r\n") or 127 <= ord(char) <= 159 or 0xD800 <= ord(char) <= 0xDFFF for char in text):
        raise DocumentError("invalid_document")
    return text.replace("\r\n", "\n").replace("\r", "\n").strip()


def _xml(data):
    if len(data) > MAX_XML:
        raise DocumentError("document_too_complex")
    text = data.decode("utf-8-sig", errors="strict")
    if "\x00" in text or re.search(r"<!\s*(?:DOCTYPE|ENTITY)\b", text, re.I):
        raise DocumentError("invalid_document")
    return ET.fromstring(text)


def _docx(data):
    with zipfile.ZipFile(BytesIO(data)) as archive:
        entries = archive.infolist()
        names = [item.filename for item in entries]
        if len(entries) > 2048 or len(names) != len(set(names)) or sum(i.file_size for i in entries) > MAX_UNPACKED:
            raise DocumentError("document_too_complex")
        if "word/document.xml" not in names or "[Content_Types].xml" not in names:
            raise DocumentError("invalid_document")
        for item in entries:
            name = item.filename
            if name.startswith("/") or "\\" in name or ":" in name or ".." in PurePosixPath(name).parts or "\x00" in name:
                raise DocumentError("invalid_document")
            if item.flag_bits & 1:
                raise DocumentError("encrypted_document")
            if item.file_size > MAX_UNPACKED or (item.file_size > 65536 and item.file_size > max(1, item.compress_size) * 200):
                raise DocumentError("document_too_complex")
            if name.lower().endswith("vbaproject.bin") or "/embeddings/" in name.lower():
                raise DocumentError("unsupported_document")
            if name.endswith(".rels"):
                for relation in _xml(archive.read(item)):
                    if relation.get("TargetMode", "").lower() == "external" and not relation.get("Type", "").endswith("/hyperlink"):
                        raise DocumentError("unsupported_document")
        types = _xml(archive.read("[Content_Types].xml"))
        expected = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"
        if not any(child.get("PartName") == "/word/document.xml" and child.get("ContentType") == expected for child in types):
            raise DocumentError("unsupported_document")
        document = _xml(archive.read("word/document.xml"))
        if document.tag != W + "document" or document.find(W + "body") is None:
            raise DocumentError("invalid_document")
        for number, paragraph in _paragraphs(document.find(W + "body")):
            text = "".join(_visible_docx_text(paragraph))
            yield text, f"paragraph:{number}"


def _paragraphs(body):
    number = 0
    def walk(node, excluded=False):
        nonlocal number
        excluded = excluded or node.tag in EXCLUDED
        if node.tag == W + "p":
            number += 1
            if not excluded:
                yield number, node
        for child in node:
            yield from walk(child, excluded)
    yield from walk(body)


def _visible_docx_text(node):
    if node.tag in EXCLUDED:
        return
    if node.tag == W + "r":
        hidden = node.find(W + "rPr/" + W + "vanish")
        if hidden is not None and hidden.get(W + "val", "true") not in {"0", "false", "off"}:
            return
    if node.tag == W + "t":
        yield node.text or ""
    elif node.tag == W + "tab":
        yield "\t"
    elif node.tag in {W + "br", W + "cr"}:
        yield "\n"
    for child in node:
        yield from _visible_docx_text(child)


def _pdf(data):
    if not data.startswith(b"%PDF-"):
        raise DocumentError("invalid_document")
    from pypdf import PdfReader
    # pypdf diagnostics can contain file-supplied text; the worker also discards stderr.
    logging.getLogger("pypdf").disabled = True
    reader = PdfReader(BytesIO(data), strict=True)
    if reader.is_encrypted:
        raise DocumentError("encrypted_document")
    if len(reader.pages) > 200:
        raise DocumentError("document_too_complex")
    decoded_bytes = 0
    for number, page in enumerate(reader.pages, 1):
        contents = page.get_contents()
        if contents is not None:
            decoded_bytes += len(contents.get_data())
            if decoded_bytes > MAX_UNPACKED:
                raise DocumentError("document_too_complex")
        yield page.extract_text() or "", f"page:{number}"


def parse_document(content: bytes, filename: str) -> ParsedDocument:
    kind = document_kind(filename)
    if not isinstance(content, bytes):
        raise DocumentError("invalid_document")
    if len(content) > MAX_BYTES:
        raise DocumentError("document_too_large")
    if not content:
        raise DocumentError("empty_document")
    chunks = []
    total = 0
    try:
        if kind in {"txt", "md"}:
            sections = [(content.decode("utf-8-sig", errors="strict"), "lines")]
        else:
            sections = _pdf(content) if kind == "pdf" else _docx(content)
        for raw, locator in sections:
            total += len(raw)
            if total > MAX_TEXT:
                raise DocumentError("extracted_text_too_large")
            text = _clean_text(raw)
            normalized = raw.replace("\r\n", "\n").replace("\r", "\n")
            leading_lines = normalized[:len(normalized) - len(normalized.lstrip())].count("\n")
            start = 0
            while start < len(text):
                end = min(start + CHUNK_SIZE, len(text))
                location = locator
                if locator == "lines":
                    location = f"lines:{leading_lines + text.count(chr(10), 0, start) + 1}-{leading_lines + text.count(chr(10), 0, end - 1) + 1}"
                if text[start:end].strip():
                    chunks.append(DocumentChunk(len(chunks), text[start:end], location))
                if len(chunks) > MAX_CHUNKS:
                    raise DocumentError("document_too_complex")
                if end == len(text):
                    break
                start = end - OVERLAP
        if not chunks:
            raise DocumentError("ocr_required" if kind == "pdf" else "empty_document")
        return ParsedDocument(kind, tuple(chunks))
    except DocumentError:
        raise
    except (MemoryError, RecursionError):
        raise DocumentError("document_too_complex") from None
    except Exception:
        raise DocumentError("invalid_document") from None
