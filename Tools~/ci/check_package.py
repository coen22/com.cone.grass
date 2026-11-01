"""Package hygiene check for Cone packages (stdlib only). Run from the repository root:

    python3 Tools~/ci/check_package.py

Exits 1 and lists every problem when the package is not release-shaped: manifest fields, the documentation skeleton,
changelog, licence and README, `.meta` files for everything Unity imports, no absolute paths or evidence folders, and the
shared `.editorconfig` byte for byte.
"""
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path

# SHA-256 of lanes-1101/templates/.editorconfig, the shared Cone C# style (owner, 2026-11-01).
EDITORCONFIG_SHA256 = "516f22e6b2b78647d503789ca96f984abcc93f046201ee955c64656de067583a"

REQUIRED_MANIFEST = ["name", "displayName", "version", "unity", "description", "author", "keywords",
                     "documentationUrl", "changelogUrl", "licensesUrl"]
REQUIRED_DOCS = ["index.md", "getting-started.md", "concepts.md", "reference.md", "performance.md", "troubleshooting.md"]
README_SECTIONS = ["Features", "Requirements", "Install", "Quick start", "Samples", "Documentation", "License"]
# Folders Unity imports from a package: everything in them needs a .meta file.
IMPORTED_ROOTS = ["Runtime", "Editor", "Tests"]
# Never shipped: evidence, sealed packets and validation fixtures belong in pull requests and issues.
FORBIDDEN_DIRS = {"Evidence", "Evidence~", "Validation~", "PortableHosts", "Packets~", "Sealed~"}
TEXT_SUFFIXES = {".cs", ".md", ".json", ".asmdef", ".asmref", ".uss", ".uxml", ".yml", ".yaml", ".txt", ".py",
                 ".shader", ".hlsl", ".cginc", ".compute", ".unity", ".prefab", ".asset", ".mat", ".meta", ".ps1", ".sh", ".props", ".targets", ".csproj", ".slnx", ".sln", ".globalconfig"}
# Developer-machine paths. Generic install locations such as C:\Program Files are allowed in tooling defaults.
# Built from parts so this file's own source never matches it.
_SEP = r"[\\/]+"
_DRIVE_ROOTS = r"(?:users|k|projects|work|dev|src)"
_HOME = r"[^/\s]+/"
ABSOLUTE_PATH = re.compile(r"(?i)(?:\b[a-z]:" + _SEP + _DRIVE_ROOTS + _SEP + r")|(?:(?<![\w./])/" + "Users/" + _HOME + r")|(?:(?<![\w./])/" + "home/" + _HOME + ")")
# A [SerializeField]/[SerializeReference] attribute (alone or in a list) and a readonly field declaration.
SERIALIZE_ATTRIBUTE = re.compile(r"\[[^\]]*\b(?:SerializeField|SerializeReference)\b[^\]]*\]")
STATIC_MEMBER = re.compile(r"\bstatic\b")
READONLY_FIELD = re.compile(r"\breadonly\b[^(){}]*[;=]")
OPAQUE_BUNDLES = {".androidlib", ".androidpack", ".bundle", ".framework", ".xcframework", ".plugin", ".aar"}
SEMVER = re.compile(r"^\d+\.\d+\.\d+$")


def tracked_files(root: Path):
    """Files the repository ships: git's tracked files when available, otherwise everything outside .git."""
    try:
        out = subprocess.run(["git", "ls-files", "-z"], cwd=root, capture_output=True, check=True).stdout
        return [root / p for p in out.decode("utf-8").split(chr(0)) if p]
    except (OSError, subprocess.CalledProcessError):
        return [p for p in root.rglob("*") if p.is_file() and ".git" not in p.relative_to(root).parts]


def main(root: Path) -> int:
    problems = []
    add = problems.append

    manifest_path = root / "package.json"
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        print(f"package.json: unreadable ({error})")
        return 1
    for key in REQUIRED_MANIFEST:
        if not manifest.get(key):
            add(f"package.json: missing '{key}'")
    version = str(manifest.get("version", ""))
    if not SEMVER.match(version):
        add(f"package.json: version '{version}' is not a release version (MAJOR.MINOR.PATCH, no suffix)")
    if "preview" in json.dumps(manifest).lower():
        add("package.json: contains 'preview'")
    for name, value in (manifest.get("dependencies") or {}).items():
        if str(value).startswith("file:") or "preview" in str(value):
            add(f"package.json: dependency {name} -> {value} is not a released version")
    for sample in manifest.get("samples") or []:
        path = sample.get("path", "")
        if not path or not (root / path).is_dir():
            add(f"package.json: sample '{sample.get('displayName')}' path '{path}' does not exist")
        elif not (root / path / "README.md").is_file():
            add(f"{path}: sample has no README.md")

    docs = root / "Documentation~"
    for name in REQUIRED_DOCS:
        if not (docs / name).is_file():
            add(f"Documentation~/{name}: missing")

    changelog = root / "CHANGELOG.md"
    if not changelog.is_file():
        add("CHANGELOG.md: missing")
    elif f"## [{version}]" not in changelog.read_text(encoding="utf-8"):
        add(f"CHANGELOG.md: no '## [{version}]' entry")
    if not (root / "LICENSE.md").is_file():
        add("LICENSE.md: missing")
    readme = root / "README.md"
    if not readme.is_file():
        add("README.md: missing")
    else:
        headings = [line[3:].strip() for line in readme.read_text(encoding="utf-8").splitlines() if line.startswith("## ")]
        found = [h for h in headings if h in README_SECTIONS]
        missing = [s for s in README_SECTIONS if s not in headings]
        if missing:
            add(f"README.md: missing sections {missing}")
        elif found != README_SECTIONS:
            add(f"README.md: sections out of order {found}, expected {README_SECTIONS}")

    editorconfig = root / ".editorconfig"
    if not editorconfig.is_file():
        add(".editorconfig: missing")
    elif hashlib.sha256(editorconfig.read_bytes()).hexdigest() != EDITORCONFIG_SHA256:
        add(".editorconfig: differs from the shared template")

    files = tracked_files(root)
    shipped = {p.relative_to(root).as_posix() for p in files}
    dirs = {Path(rel).parent.as_posix() for rel in shipped}
    for parent in list(dirs):
        while parent not in (".", ""):
            dirs.add(parent)
            parent = Path(parent).parent.as_posix()
    sample_roots = {s.get("path", "") for s in manifest.get("samples") or []}
    for rel in sorted(shipped | dirs):
        parts = Path(rel).parts
        if not parts or rel.endswith(".meta") or rel in sample_roots:
            continue
        # Hidden (.git, .github) and tilde (Documentation~, Samples~, Tools~) paths are not imported; a sample's
        # contents are, once the Package Manager copies the sample into Assets.
        inner = parts
        for root_path in sample_roots:
            if root_path and rel.startswith(root_path + "/"):
                inner = Path(rel).relative_to(root_path).parts
                break
        else:
            if any(part.endswith("~") for part in parts):
                continue
        if any(part.endswith("~") or part.startswith(".") for part in inner):
            continue
        # Plugin bundles (.androidlib, .bundle, .framework...) are one asset to Unity: only the bundle has a .meta.
        if any(Path(part).suffix in OPAQUE_BUNDLES for part in inner[:-1]):
            continue
        if rel + ".meta" not in shipped:
            add(f"{rel}: no .meta file")
    for rel in shipped:
        if rel.endswith(".meta") and rel[:-5] not in shipped and rel[:-5] not in dirs:
            add(f"{rel}: orphan .meta")

    for rel in sorted(dirs):
        if Path(rel).name in FORBIDDEN_DIRS:
            add(f"{rel}: evidence/validation folder must not ship")
    for path in files:
        rel = path.relative_to(root).as_posix()
        if rel in ("Tools~/ci/check_package.py", "Tools~/ci/test_check_package.py"):  # the checker and its fixtures
            continue
        if path.suffix.lower() in TEXT_SUFFIXES and path.is_file() and path.stat().st_size < 4_000_000:
            try:
                text = path.read_text(encoding="utf-8")
            except UnicodeDecodeError:
                continue
            for number, line in enumerate(text.splitlines(), 1):
                if ABSOLUTE_PATH.search(line):
                    add(f"{rel}:{number}: developer-machine absolute path")

    # Unity never serializes readonly fields: a [SerializeField] that dotnet format made readonly silently loses its data.
    for path in files:
        rel = path.relative_to(root).as_posix()
        if path.suffix != ".cs" or rel.startswith("Tools~/") or not path.is_file():
            continue
        pending = False
        for number, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
            code = line.split("//", 1)[0].strip()
            if not code:
                continue
            # Peel every leading attribute list ([A][B, C] ...); what remains, if anything, is the declaration.
            while code.startswith("["):
                close = code.find("]")
                if close < 0:
                    break
                if SERIALIZE_ATTRIBUTE.match(code[:close + 1]):
                    pending = True
                code = code[close + 1:].strip()
            if not code:
                continue
            # Unity serializes instance fields only, so a static readonly field is never a serialized one.
            if pending and READONLY_FIELD.search(code) and not STATIC_MEMBER.search(code):
                add(f"{rel}:{number}: readonly field with [SerializeField]/[SerializeReference]; Unity drops its data")
            pending = False

    for problem in problems:
        print(problem)
    print(f"{manifest.get('name')} {version}: {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(Path(sys.argv[1] if len(sys.argv) > 1 else ".").resolve()))
