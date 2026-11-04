"""Actual text/PDF/OOXML fixtures; no external model, storage or document access."""
import asyncio
from io import BytesIO
import json
import zipfile

import pytest
from fastapi.testclient import TestClient
from pypdf import PdfWriter
from pypdf.generic import DecodedStreamObject, DictionaryObject, NameObject

from knowledge_backend.documents import DocumentError, parse_document
from knowledge_backend.main import create_app, parse_isolated

KEY = "fixture-parser-key-private"


def pdf(text="Factory A production 300", *, encrypted=False, empty=False):
    writer = PdfWriter()
    page = writer.add_blank_page(width=600, height=800)
    if not empty:
        font = DictionaryObject({NameObject("/Type"): NameObject("/Font"), NameObject("/Subtype"): NameObject("/Type1"),
                                 NameObject("/BaseFont"): NameObject("/Helvetica")})
        page[NameObject("/Resources")] = DictionaryObject({NameObject("/Font"): DictionaryObject({NameObject("/F1"): writer._add_object(font)})})
        stream = DecodedStreamObject()
        stream.set_data(f"BT /F1 12 Tf 20 780 Td ({text}) Tj ET".encode("ascii"))
        page[NameObject("/Contents")] = writer._add_object(stream)
    if encrypted:
        writer.encrypt("secret")
    result = BytesIO()
    writer.write(result)
    return result.getvalue()


def docx(text="工厂 A 产量 300", *, xml=None, extra=None):
    result = BytesIO()
    with zipfile.ZipFile(result, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("[Content_Types].xml", '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        archive.writestr("word/document.xml", xml or f'<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:body></w:document>')
        for name, value in (extra or {}).items():
            archive.writestr(name, value)
    return result.getvalue()


def error(code, data, filename):
    with pytest.raises(DocumentError) as failure:
        parse_document(data, filename)
    assert failure.value.code == code
    assert "secret" not in str(failure.value)


def test_real_formats_and_source_locations():
    for filename, data, kind, text, locator in [
        ("说明.TXT", "\ufeff工厂 A\r\n产量 300".encode(), "txt", "工厂 A\n产量 300", "lines:1-2"),
        ("说明.md", "# 生产说明\n产量 300".encode(), "md", "# 生产说明\n产量 300", "lines:1-2"),
        ("说明.pdf", pdf(), "pdf", "Factory A production 300", "page:1"),
        ("说明.docx", docx(), "docx", "工厂 A 产量 300", "paragraph:1"),
    ]:
        result = parse_document(data, filename)
        assert result.kind == kind
        assert result.chunks[0].text == text
        assert result.chunks[0].locator == locator
        assert [chunk.index for chunk in result.chunks] == list(range(len(result.chunks)))


def test_deterministic_unicode_chunks_preserve_overlap_and_lines():
    text = "😀生产线甲\n" * 800
    first = parse_document(text.encode(), "a.md")
    second = parse_document(text.encode(), "a.md")
    assert first == second and len(first.chunks) > 1
    assert all(0 < len(chunk.text) <= 1200 for chunk in first.chunks)
    assert first.chunks[0].text[-150:] == first.chunks[1].text[:150]
    assert all(chunk.locator.startswith("lines:") for chunk in first.chunks)
    rebuilt = first.chunks[0].text + "".join(chunk.text[150:] for chunk in first.chunks[1:])
    assert rebuilt == text.rstrip()


def test_line_references_include_leading_blank_lines():
    result = parse_document(b"\r\n\r\n  first\nsecond\n", "a.txt")
    assert result.chunks[0].text == "first\nsecond"
    assert result.chunks[0].locator == "lines:3-4"


@pytest.mark.parametrize("filename", ["a.doc", "a.html", "../a.txt", "a/b.txt", "a\\b.txt", "a.txt\x00", "CON.txt", "a.txt ", "", "a" * 256 + ".txt"])
def test_reject_unsupported_or_unsafe_names(filename):
    with pytest.raises(DocumentError):
        parse_document(b"hello", filename)


def test_text_and_document_limits():
    error("invalid_document", b"\xffprivate-secret", "a.txt")
    error("invalid_document", b"binary\0secret", "a.md")
    error("empty_document", b" \r\n\t", "a.txt")
    error("document_too_large", b"a" * (5 * 1024 * 1024 + 1), "a.txt")
    error("extracted_text_too_large", b"a" * 1_000_001, "a.txt")


def test_pdf_failures_are_safe():
    error("encrypted_document", pdf(encrypted=True), "a.pdf")
    error("ocr_required", pdf(empty=True), "a.pdf")
    error("invalid_document", b"not-pdf-private-secret", "a.pdf")


def test_docx_rejects_entities_external_relationships_and_active_content():
    evil = '<?xml version="1.0"?><!DOCTYPE a [<!ENTITY x SYSTEM "file:///private-secret">]><a>&x;</a>'
    error("invalid_document", docx(xml=evil), "a.docx")
    external = '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" TargetMode="External" Target="https://private-secret.invalid" Type="image"/></Relationships>'
    error("unsupported_document", docx(extra={"word/_rels/document.xml.rels": external}), "a.docx")
    error("unsupported_document", docx(extra={"word/vbaProject.bin": b"macro"}), "a.docx")
    error("invalid_document", docx(extra={"../private-secret": b"x"}), "a.docx")
    error("document_too_complex", docx(extra={"word/unused.xml": b"a" * (16 * 1024 * 1024 + 1)}), "a.docx")


def test_docx_paragraph_and_table_text_without_deleted_revisions():
    xml = '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:del><w:r><w:delText>obsolete</w:delText></w:r></w:del><w:r><w:t>current</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>table value</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>'
    result = parse_document(docx(xml=xml), "a.docx")
    assert [(c.text, c.locator) for c in result.chunks] == [("current", "paragraph:1"), ("table value", "paragraph:2")]


def test_deleted_or_hidden_docx_runs_never_become_visible_text():
    xml = '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:del><w:r><w:t>deleted-secret</w:t></w:r></w:del><w:r><w:rPr><w:vanish/></w:rPr><w:t>hidden-secret</w:t></w:r><w:r><w:t>visible</w:t></w:r></w:p></w:body></w:document>'
    result = parse_document(docx(xml=xml), "a.docx")
    assert result.chunks[0].text == "visible"


def test_docx_deleted_paragraphs_and_textboxes_do_not_leak_or_duplicate_text():
    xml = '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:del><w:p><w:r><w:t>deleted</w:t></w:r></w:p></w:del><w:p><w:r><w:t>visible</w:t></w:r><w:r><w:drawing><w:txbxContent><w:p><w:r><w:t>textbox</w:t></w:r></w:p></w:txbxContent></w:drawing></w:r></w:p></w:body></w:document>'
    result = parse_document(docx(xml=xml), "a.docx")
    assert [(chunk.text, chunk.locator) for chunk in result.chunks] == [("visible", "paragraph:2")]


@pytest.mark.parametrize("reason", ["timeout", "cancelled"])
def test_aborted_isolated_work_reaps_child(monkeypatch, reason):
    async def exercise():
        import knowledge_backend.main as service
        original = service.asyncio.create_subprocess_exec
        processes = []
        async def record(*args, **kwargs):
            process = await original(*args, **kwargs)
            processes.append(process)
            return process
        monkeypatch.setattr(service.asyncio, "create_subprocess_exec", record)
        if reason == "timeout":
            with pytest.raises(DocumentError) as failure:
                await parse_isolated(pdf(), "a.pdf", timeout=0.00001)
            assert failure.value.code == "parser_timeout"
        else:
            task = asyncio.create_task(parse_isolated(pdf(), "a.pdf"))
            while not processes:
                await asyncio.sleep(0)
            task.cancel()
            with pytest.raises(asyncio.CancelledError):
                await task
        assert len(processes) == 1 and processes[0].returncode is not None
    asyncio.run(exercise())


def test_http_requires_private_key_and_strict_request(monkeypatch):
    monkeypatch.setenv("ASSEMBLEAI_KNOWLEDGE_PARSER_KEY", KEY)
    with TestClient(create_app()) as client:
        assert client.post("/internal/parse?filename=a.txt", content=b"text").status_code == 401
        headers = {"Authorization": "Bearer " + KEY, "Content-Type": "application/octet-stream"}
        for path, extra in [("/internal/parse?filename=a.txt&tenant=evil", {}), ("/internal/parse?filename=a.txt&filename=b.txt", {}),
                            ("/internal/parse?filename=a.txt", {"Content-Encoding": "gzip"})]:
            response = client.post(path, content=b"text", headers=headers | extra)
            assert response.status_code == 400
        response = client.post("/internal/parse?filename=a.txt", content=b"text", headers=headers)
        assert response.status_code == 200
        assert response.json() == {"kind": "txt", "chunks": [{"index": 0, "text": "text", "locator": "lines:1-1"}]}
        assert response.headers["cache-control"] == "no-store"
        assert client.get("/health/ready").status_code == 200
    monkeypatch.delenv("ASSEMBLEAI_KNOWLEDGE_PARSER_KEY")
    with TestClient(create_app()) as client:
        assert client.get("/health/ready").status_code == 503
        assert client.post("/internal/parse?filename=a.txt", content=b"text", headers=headers).status_code == 503


def test_isolated_worker_parses_actual_formats_and_maps_errors():
    for filename, content in [("a.pdf", pdf()), ("a.docx", docx())]:
        result = asyncio.run(parse_isolated(content, filename))
        assert result["chunks"] and result["kind"] == filename.split(".")[-1]
    with pytest.raises(DocumentError) as failure:
        asyncio.run(parse_isolated(pdf(encrypted=True), "a.pdf"))
    assert failure.value.code == "encrypted_document"
