from pathlib import Path

import shutil
import sys

try:
    import jsonc
except ModuleNotFoundError as e:
    raise ImportError(
        "Missing dependency 'json-with-comments' (imported as 'jsonc').\n"
        f"Install it with:\n  {sys.executable} -m pip install json-with-comments\n"
        "Or add it to your project's requirements."
    ) from e

from configure import configure_ocr_model
from install_first_run import install_first_run


working_dir = Path(__file__).parent.parent.resolve()
install_path = working_dir / Path("install")

# the first parameter is self name
if sys.argv.__len__() < 4:
    print("Usage: python install.py <version> <os> <arch>")
    print("Example: python install.py v1.0.0 win x86_64")
    sys.exit(1)

version = sys.argv[1]
os_name = sys.argv[2]
arch = sys.argv[3]


def get_dotnet_platform_tag():
    """自动检测当前平台并返回对应的dotnet平台标签"""
    if os_name == "win" and arch == "x86_64":
        platform_tag = "win-x64"
    elif os_name == "win" and arch == "aarch64":
        platform_tag = "win-arm64"
    elif os_name == "macos" and arch == "x86_64":
        platform_tag = "osx-x64"
    elif os_name == "macos" and arch == "aarch64":
        platform_tag = "osx-arm64"
    elif os_name == "linux" and arch == "x86_64":
        platform_tag = "linux-x64"
    elif os_name == "linux" and arch == "aarch64":
        platform_tag = "linux-arm64"
    else:
        print("Unsupported OS or architecture.")
        print("available parameters:")
        print("version: e.g., v1.0.0")
        print("os: [win, macos, linux, android]")
        print("arch: [aarch64, x86_64]")
        sys.exit(1)

    return platform_tag


def install_deps():
    if not (working_dir / "deps" / "bin").exists():
        print('Please download the MaaFramework to "deps" first.')
        print('请先下载 MaaFramework 到 "deps"。')
        sys.exit(1)

    if os_name == "android":
        shutil.copytree(
            working_dir / "deps" / "bin",
            install_path,
            dirs_exist_ok=True,
        )
        shutil.copytree(
            working_dir / "deps" / "share" / "MaaAgentBinary",
            install_path / "MaaAgentBinary",
            dirs_exist_ok=True,
        )
    else:
        shutil.copytree(
            working_dir / "deps" / "bin",
            install_path / "runtimes" / get_dotnet_platform_tag() / "native",
            ignore=shutil.ignore_patterns(
                "*MaaDbgControlUnit*",
                "*MaaThriftControlUnit*",
                "*MaaRpc*",
                "*MaaHttp*",
                "plugins",
                "*.node",
                "*MaaPiCli*",
            ),
            dirs_exist_ok=True,
        )
        shutil.copytree(
            working_dir / "deps" / "share" / "MaaAgentBinary",
            install_path / "libs" / "MaaAgentBinary",
            dirs_exist_ok=True,
        )
        shutil.copytree(
            working_dir / "deps" / "bin" / "plugins",
            install_path / "plugins" / get_dotnet_platform_tag(),
            dirs_exist_ok=True,
        )



def install_resource():

    configure_ocr_model()

    shutil.copytree(
        working_dir / "assets" / "resource",
        install_path / "resource",
        ignore=shutil.ignore_patterns("UserStages"),
        dirs_exist_ok=True,
    )
    shutil.copy2(
        working_dir / "assets" / "interface.json",
        install_path,
    )

    for icon_filename in ("logo.png", "logo.ico"):
        shutil.copy2(working_dir / "assets" / icon_filename, install_path)

    with open(install_path / "interface.json", "r", encoding="utf-8") as f:
        interface = jsonc.load(f)

    # Branch builds keep the source version for in-app update comparisons.
    # The CI suffix still identifies the archive produced by the workflow.
    if version and "-ci." not in version:
        interface["version"] = version
    # The source interface lives under assets/, while the packaged interface
    # and agent/ directory are siblings in the release root.
    if os_name == "win":
        interface["agent"]["child_exec"] = "./python/python.exe"
    elif os_name in {"macos", "linux"}:
        interface["agent"]["child_exec"] = "./python/bin/python3"
    interface["agent"]["child_args"] = ["./agent/main.py"]

    with open(install_path / "interface.json", "w", encoding="utf-8") as f:
        jsonc.dump(interface, f, ensure_ascii=False, indent=4)


def install_chores():
    # Never include user settings in an update payload. The startup hook inserts
    # missing defaults on disk before MFA reads settings, preserving false/empty.
    if (install_path / "appsettings.json").exists() or any((install_path / "config").rglob("*")):
        raise RuntimeError("构建目录含用户配置；请使用干净的发布暂存目录，不得将配置打入更新包")
    if any(path.is_file() and path.name != ".gitkeep" for path in (install_path / "userdata").rglob("*")) or any(
        path.is_file() for path in (install_path / "resource/image/UserStages").rglob("*")
    ):
        raise RuntimeError("构建目录含用户截图；请使用干净的发布暂存目录")
    shutil.copytree(
        working_dir / "assets/userdata", install_path / "userdata",
        ignore=shutil.ignore_patterns("UserStages"), dirs_exist_ok=True,
    )
    shutil.copy2(
        working_dir / "README.md",
        install_path,
    )
    shutil.copy2(
        working_dir / "LICENSE",
        install_path,
    )
    if os_name != "android":
        install_first_run(install_path, working_dir / "build" / "first-run-hook" / "TkfmFirstRun.dll")


def install_agent():
    shutil.copytree(
        working_dir / "agent",
        install_path / "agent",
        dirs_exist_ok=True,
    )


if __name__ == "__main__":
    install_deps()
    install_resource()
    install_chores()
    install_agent()

    print(f"Install to {install_path} successfully.")
