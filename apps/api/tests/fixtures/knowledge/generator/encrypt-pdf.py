"""Writes ../encrypted-return-policy.pdf: ../return-policy.pdf with a password to open it.

Needs pypdf (BSD-3-Clause) with cryptography (Apache-2.0 OR BSD-3-Clause) for AES-256, in a
throwaway virtual environment; see ../README.md. The tests only read the committed output.

    python3 encrypt-pdf.py
"""

from pathlib import Path

from pypdf import PdfReader, PdfWriter

here = Path(__file__).resolve().parent
source = here.parent / "return-policy.pdf"
target = here.parent / "encrypted-return-policy.pdf"

writer = PdfWriter(clone_from=PdfReader(source))
# A user password (needed to open the file), as when an owner exports a protected PDF;
# AES-256 is what current PDF software uses for that.
writer.encrypt(user_password="anxin-1234", owner_password="anxin-owner-5678", algorithm="AES-256")
with target.open("wb") as output:
    writer.write(output)

print(f"{target.name}: {target.stat().st_size} bytes")
