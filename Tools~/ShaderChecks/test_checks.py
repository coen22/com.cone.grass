import io
from pathlib import Path
import sys
import tarfile
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import check
import prepare


PROGRAM = """
HLSLPROGRAM
#pragma target 4.5
#pragma vertex Vertex
#pragma fragment Fragment
float4 Vertex(uint id : SV_VertexID) : SV_Position { return 0; }
float4 Fragment() : SV_Target { return 0; }
ENDHLSL
"""


class ShaderExtractionTests(unittest.TestCase):
    def extract(self, source):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            shader = root / "Sample.shader"
            shader.write_text(source)
            return list(check.shader_programs(shader, root))

    def test_commented_markers_do_not_add_programs(self):
        source = 'Shader "Example" { SubShader { Pass { Name "Actual"\n'
        source += '// HLSLPROGRAM\n/* HLSLINCLUDE ENDHLSL */\n' + PROGRAM + ' } } }'
        programs = self.extract(source)
        self.assertEqual(["vertex", "fragment"], [item["stage"] for item in programs])
        self.assertTrue(all(item["pass"] == "Actual" for item in programs))
        self.assertIn('#line 5 "Sample.shader"', programs[0]["source"])

    def test_shared_include_and_original_program_are_preserved(self):
        shared = '\nfloat SharedValue() { return 3; }\n'
        source = 'Shader "Example" { SubShader { HLSLINCLUDE' + shared
        source += 'ENDHLSL\nPass { ' + PROGRAM + ' } } }'
        programs = self.extract(source)
        self.assertIn(shared, programs[0]["source"])
        self.assertIn('float4 Vertex(uint id : SV_VertexID)', programs[0]["source"])

    def test_unnamed_pass_does_not_inherit_previous_pass_name(self):
        source = 'Shader "Example" { SubShader { Pass { Name "First"\n' + PROGRAM
        source += '} Pass { ' + PROGRAM + ' } } }'
        programs = self.extract(source)
        self.assertEqual(["First", "First", "pass_1", "pass_1"], [item["pass"] for item in programs])

    def test_pass_scoped_include_is_rejected(self):
        source = 'Shader "Example" { SubShader { Pass { HLSLINCLUDE\nfloat scoped;\nENDHLSL\n'
        source += PROGRAM + ' } } }'
        with self.assertRaisesRegex(ValueError, "Scoped HLSLINCLUDE"):
            self.extract(source)

    def test_multiple_subshaders_are_rejected(self):
        source = 'Shader "Example" { SubShader { Pass {' + PROGRAM + '} }'
        source += 'SubShader { Pass {' + PROGRAM + '} } }'
        with self.assertRaisesRegex(ValueError, "exactly one SubShader"):
            self.extract(source)

    def test_missing_entry_or_target_is_rejected(self):
        for removed in ("#pragma target 4.5", "#pragma fragment Fragment"):
            with self.subTest(removed=removed), self.assertRaises(ValueError):
                self.extract('Shader "Example" { SubShader { Pass {' + PROGRAM.replace(removed, "") + '} } }')

    def test_unterminated_program_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "Unclosed HLSL block"):
            self.extract('Shader "Example" { SubShader { Pass {' + PROGRAM.replace("ENDHLSL", "") + '} } }')

    def test_compute_kernel_defines_are_not_lost(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "Test.compute"
            source.write_text('// #pragma kernel Comment\n#pragma kernel First FEATURE=2\n#pragma kernel Second\n')
            programs = list(check.compute_programs(source, root))
        self.assertEqual(["First", "Second"], [item["entry"] for item in programs])
        self.assertEqual(["FEATURE=2"], programs[0]["keywords"])

    def test_backend_changes_platform_headers_and_preserves_source_target(self):
        program = {"stage": "fragment", "path": "Runtime/Shaders/Example.shader",
                   "entry": "Fragment", "target": 45, "keywords": ["_GRASS_GROUND_NORMAL"]}
        dxil = check.command(Path("dxc"), Path("input.hlsl"), Path("out.dxil"), program,
                             "dxil", Path("repo"), Path("graphics"))
        spirv = check.command(Path("dxc"), Path("input.hlsl"), Path("out.spirv"), program,
                              "spirv", Path("repo"), Path("graphics"))
        self.assertIn("SHADER_API_D3D11=1", dxil)
        self.assertIn("SHADER_API_VULKAN=1", spirv)
        self.assertIn("-spirv", spirv)
        self.assertIn("SHADER_TARGET=45", spirv)
        self.assertIn("UNITY_VERSION=60060000", spirv)
        self.assertIn("SHADER_STAGE_FRAGMENT=1", spirv)
        self.assertIn("_GRASS_GROUND_NORMAL", spirv)


class CompilerArchiveTests(unittest.TestCase):
    def test_archive_cannot_escape_destination(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = root / "bad.tar.gz"
            with tarfile.open(archive, "w:gz") as stream:
                member = tarfile.TarInfo("../outside")
                member.size = 1
                stream.addfile(member, io.BytesIO(b"x"))
            with self.assertRaisesRegex(ValueError, "Unsafe compiler archive member"):
                prepare.extract_compiler(archive, root / "out")
            self.assertFalse((root / "outside").exists())

    def test_archive_links_are_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            archive = root / "link.tar.gz"
            with tarfile.open(archive, "w:gz") as stream:
                member = tarfile.TarInfo("dxc")
                member.type = tarfile.SYMTYPE
                member.linkname = "/bin/sh"
                stream.addfile(member)
            with self.assertRaisesRegex(ValueError, "Unsafe compiler archive member"):
                prepare.extract_compiler(archive, root / "out")

    def test_compiler_fingerprint_includes_shared_libraries(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "bin").mkdir()
            (root / "lib").mkdir()
            (root / "bin/dxc").write_bytes(b"binary")
            (root / "lib/libdxcompiler.so").write_bytes(b"library")
            before = prepare.compiler_files(root)
            (root / "lib/libdxcompiler.so").write_bytes(b"modified")
            after = prepare.compiler_files(root)
        self.assertEqual(before["bin/dxc"], after["bin/dxc"])
        self.assertNotEqual(before["lib/libdxcompiler.so"], after["lib/libdxcompiler.so"])


if __name__ == "__main__":
    unittest.main()
