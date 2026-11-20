{
  description = "VCL — visual communication language + WebXR visualizer";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" "x86_64-darwin" "aarch64-darwin" ];
      forAll = f: nixpkgs.lib.genAttrs systems (s: f s nixpkgs.legacyPackages.${s});

      # The Unity client's Android toolchain (Quest builds): the SDK, NDK r23b
      # and JDK 11 that Unity 2022.3 expects. The editor itself comes from the
      # NixOS config (`unity`); clients/unity/Assets/Vcl/Editor/VclBuild.cs
      # points it at these through the VCL_ANDROID_* variables. Linux only,
      # where the editor runs:
      #   nix develop .#unity -c clients/unity/tools/build-quest.sh
      unityShell = system:
        let
          pkgs = import nixpkgs {
            inherit system;
            config = { allowUnfree = true; android_sdk.accept_license = true; };
          };
          android = pkgs.androidenv.composeAndroidPackages {
            platformVersions = [ "32" ];
            buildToolsVersions = [ "32.0.0" ];
            includeNDK = true;
            ndkVersions = [ "23.1.7779620" ];
            includeEmulator = false;
            includeSystemImages = false;
            includeSources = false;
          };
          sdk = "${android.androidsdk}/libexec/android-sdk";
        in pkgs.mkShell {
          packages = [ android.androidsdk pkgs.jdk11 pkgs.android-tools ];
          VCL_ANDROID_SDK = sdk;
          VCL_ANDROID_NDK = "${sdk}/ndk/23.1.7779620";
          VCL_JDK = "${pkgs.jdk11}/lib/openjdk";
        };

      # Figures from code (`fig python|octave`), rendered by `vcl serve` and
      # `vcl figs` in a bubblewrap sandbox (core/src/fig.zig).
      figPython = pkgs: pkgs.python3.withPackages (p: [ p.numpy p.scipy p.matplotlib p.sympy p.networkx ]);
    in {
      devShells = forAll (system: pkgs: {
        default = pkgs.mkShell ({
          packages = [
            pkgs.zig_0_16   # parser, CLI, stream server, wasm build
            pkgs.nodejs     # viewer unit tests (node --test)
            pkgs.android-tools # adb reverse for Quest over USB
            (figPython pkgs)
            pkgs.octave
            pkgs.gnuplot
            pkgs.librsvg    # rsvg-convert: Octave figures to PNG
          ] ++ pkgs.lib.optional pkgs.stdenv.hostPlatform.isLinux pkgs.bubblewrap;
          VCL_PYTHON = "${figPython pkgs}/bin/python3";
          VCL_OCTAVE = "${pkgs.octave}/bin/octave-cli";
          VCL_GNUPLOT = "${pkgs.gnuplot}/bin";
          VCL_RSVG = "${pkgs.librsvg}/bin";
        } // pkgs.lib.optionalAttrs pkgs.stdenv.hostPlatform.isLinux { VCL_BWRAP = "${pkgs.bubblewrap}/bin/bwrap"; });
      } // nixpkgs.lib.optionalAttrs (system == "x86_64-linux") {
        unity = unityShell system;
      });
    };
}
