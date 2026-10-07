{
  description = "AscNet dev shell and Windows launcher zip";

  # The background fetch below has no hash, so flake evaluation has to be
  # allowed to use the network. tarball-ttl = 0 makes that fetch recheck
  # the latest release instead of reusing an earlier download. Accept this
  # flake config once, or pass --impure to nix build.
  nixConfig = {
    pure-eval = false;
    tarball-ttl = 0;
  };

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    rust-overlay = {
      url = "github:oxalica/rust-overlay";
      inputs.nixpkgs.follows = "nixpkgs";
    };
  };

  outputs =
    { self, nixpkgs, rust-overlay }:
    let
      system = "x86_64-linux";
      lib = nixpkgs.lib;
      pkgs = import nixpkgs {
        inherit system;
        overlays = [ rust-overlay.overlays.default ];
        # MongoDB 7 is SSPL. The predicate stays limited to that package.
        config.allowUnfreePredicate = pkg: lib.hasPrefix "mongodb" (lib.getName pkg);
      };

      # AscNet.Launcher/src/setup.rs installs Rust 1.92.0 for the Windows build.
      rustToolchain = pkgs.rust-bin.stable."1.92.0".minimal.override {
        extensions = [
          "rust-src"
          "clippy"
          "rustfmt"
        ];
        targets = [ "x86_64-pc-windows-gnu" ];
      };

      rustPlatform = pkgs.makeRustPlatform {
        cargo = rustToolchain;
        rustc = rustToolchain;
      };

      mingwCc = pkgs.pkgsCross.mingwW64.stdenv.cc;
      # Rust's windows-gnu target links `-l:libpthread.a`. Nix's MinGW uses
      # mcfgthread by default, so the static winpthreads archive is separate.
      mingwPthreads = pkgs.pkgsCross.mingwW64.windows.pthreads;
      windowsTarget = "x86_64-pc-windows-gnu";
      windowsLinker = "${mingwCc}/bin/${mingwCc.targetPrefix}gcc";
      launcherVersion = (builtins.fromTOML (builtins.readFile ./AscNet.Launcher/Cargo.toml)).package.version;

      # The MinGW wrapper also installs an unprefixed gcc. Keep the host compiler first
      # so build scripts still compile for Linux.
      hostCompilerFirst = ''
        export PATH="${pkgs.stdenv.cc}/bin''${PATH:+:}$PATH"
      '';

      windowsEnv = {
        CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER = windowsLinker;
        CC_x86_64_pc_windows_gnu = windowsLinker;
        CARGO_TARGET_X86_64_PC_WINDOWS_GNU_RUSTFLAGS = "-L native=${mingwPthreads}/lib";
        PKG_CONFIG_ALLOW_CROSS = "1";
        PKG_CONFIG_PATH = "${pkgs.openssl.dev}/lib/pkgconfig";
        OPENSSL_NO_VENDOR = "1";
        DOTNET_CLI_TELEMETRY_OPTOUT = "1";
      };

      # background.bmp, background.mp4, and background.wav are the only zip
      # members this tree cannot generate. They are taken from the latest
      # published launcher and nothing else. The executable, launcher.json,
      # setup-local.ps1, and supported-client.json are produced here.
      #
      # releases/latest changes whenever a launcher is published. A pinned
      # hash would not follow it: Nix returns the store object for the hash
      # it was given and does not download the new file. lib.fakeHash does
      # not turn that check off either. nixpkgs still verifies it, and the
      # build stops until someone pastes the hash Nix prints. This fetch
      # therefore sets no hash.
      launcherAssets = builtins.fetchurl {
        url = "https://github.com/reiserFSs/InfiniteLoop/releases/latest/download/AscNetLauncher.zip";
        name = "AscNetLauncher-latest.zip";
      };

      checkLauncherZip = pkgs.writeText "check-launcher-zip.py" ''
        import pathlib, sys, zipfile
        version, stage, archive = sys.argv[1:]
        stage = pathlib.Path(stage)
        expected = [
            "AscNetLauncher.exe",
            "background.bmp",
            "background.mp4",
            "background.wav",
            "launcher.json",
            "setup-local.ps1",
            "supported-client.json",
        ]
        exe = (stage / "AscNetLauncher.exe").read_bytes()
        if not exe.startswith(b"MZ"):
            raise SystemExit("AscNetLauncher.exe is not a Windows executable")
        marker = f"ASCNET_LAUNCHER_VERSION={version}\0".encode()
        if marker not in exe:
            raise SystemExit("AscNetLauncher.exe is missing ASCNET_LAUNCHER_VERSION")
        if (stage / "background.bmp").read_bytes()[:2] != b"BM":
            raise SystemExit("background.bmp is not a bitmap")
        wav = (stage / "background.wav").read_bytes()[:12]
        if not wav.startswith(b"RIFF") or wav[8:12] != b"WAVE":
            raise SystemExit("background.wav is not a wave file")
        if b"ftyp" not in (stage / "background.mp4").read_bytes()[:32]:
            raise SystemExit("background.mp4 is not an mp4")
        archive = pathlib.Path(archive)
        if archive.stat().st_size > 128 * 1024 * 1024:
            raise SystemExit("launcher zip exceeds 128 MiB")
        with zipfile.ZipFile(archive) as zipped:
            names = zipped.namelist()
            if sorted(names) != sorted(expected) or len(names) != len(expected):
                raise SystemExit(f"launcher zip members: {names}")
            expanded = 0
            for info in zipped.infolist():
                if info.is_dir() or info.file_size <= 0 or info.file_size > 128 * 1024 * 1024:
                    raise SystemExit(f"bad zip member {info.filename}")
                if info.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
                    raise SystemExit(f"unsupported compression for {info.filename}")
                expanded += info.file_size
            if expanded > 256 * 1024 * 1024:
                raise SystemExit("launcher zip expands past 256 MiB")
            if zipped.read("AscNetLauncher.exe") != exe:
                raise SystemExit("zipped executable does not match the built executable")
        print(f"AscNetLauncher.zip {version} {archive.stat().st_size} bytes")
      '';

      ascnet-launcher = pkgs.stdenv.mkDerivation {
        pname = "ascnet-launcher";
        version = launcherVersion;
        src = ./AscNet.Launcher;
        cargoDeps = rustPlatform.importCargoLock {
          lockFile = ./AscNet.Launcher/Cargo.lock;
        };
        nativeBuildInputs = [
          rustPlatform.cargoSetupHook
          rustToolchain
          mingwCc
          pkgs.pkg-config
          pkgs.openssl
        ];
        hardeningDisable = [ "all" ];
        # patchelf and strip do not understand PE binaries. Cargo's release profile strips.
        dontFixup = true;
        env = windowsEnv // {
          CARGO_NET_OFFLINE = "true";
        };
        preBuild = hostCompilerFirst;
        buildPhase = ''
          runHook preBuild
          cargo build --release --offline --target ${windowsTarget} -j "$NIX_BUILD_CORES"
          runHook postBuild
        '';
        installPhase = ''
          runHook preInstall
          mkdir -p "$out/bin"
          cp "target/${windowsTarget}/release/ascnet-launcher.exe" "$out/bin/ascnet-launcher.exe"
          runHook postInstall
        '';
      };

      ascnet-launcher-zip = pkgs.stdenv.mkDerivation {
        pname = "ascnet-launcher-zip";
        version = launcherVersion;
        src = ./AscNet.Launcher;
        nativeBuildInputs = [
          pkgs.unzip
          pkgs.zip
          pkgs.python3
        ];
        dontConfigure = true;
        dontFixup = true;
        buildPhase = ''
          runHook preBuild
          stage="$NIX_BUILD_TOP/stage"
          media="$NIX_BUILD_TOP/media"
          archive="$NIX_BUILD_TOP/AscNetLauncher.zip"
          mkdir -p "$stage" "$media"
          # Unpack only the three background files. Leave the published
          # executable and metadata in the downloaded archive.
          unzip -j -o -d "$media" ${launcherAssets} background.bmp background.mp4 background.wav
          find "$media" -type f -printf '%f\n' | sort > "$NIX_BUILD_TOP/media-names"
          printf '%s\n' background.bmp background.mp4 background.wav > "$NIX_BUILD_TOP/media-expected"
          cmp "$NIX_BUILD_TOP/media-expected" "$NIX_BUILD_TOP/media-names"
          cp "$media/background.bmp" "$media/background.mp4" "$media/background.wav" "$stage/"
          cp launcher.json setup-local.ps1 supported-client.json "$stage/"
          cp ${ascnet-launcher}/bin/ascnet-launcher.exe "$stage/AscNetLauncher.exe"
          (cd "$stage" && zip -X -9 "$archive" ${lib.concatMapStringsSep " " (name: name) [
            "AscNetLauncher.exe"
            "background.bmp"
            "background.mp4"
            "background.wav"
            "launcher.json"
            "setup-local.ps1"
            "supported-client.json"
          ]})
          python3 ${checkLauncherZip} ${launcherVersion} "$stage" "$archive"
          runHook postBuild
        '';
        installPhase = ''
          runHook preInstall
          mkdir -p "$out"
          cp "$NIX_BUILD_TOP/AscNetLauncher.zip" "$out/AscNetLauncher.zip"
          runHook postInstall
        '';
      };
    in
    {
      packages.${system} = {
        inherit ascnet-launcher ascnet-launcher-zip;
        default = ascnet-launcher-zip;
      };

      devShells.${system}.default = pkgs.mkShell {
        packages = [
          rustToolchain
          mingwCc
          pkgs.dotnet-sdk_8
          pkgs.python3
          pkgs.mitmproxy
          pkgs.pkg-config
          pkgs.openssl
          pkgs.mongodb
        ];
        env = windowsEnv;
        shellHook = hostCompilerFirst;
      };
    };
}
