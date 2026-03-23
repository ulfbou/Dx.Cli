# Formula/dxs.rb
# Homebrew formula for dxs — the DX snapshot and apply CLI tool.
#
# This formula is maintained in ulfbou/homebrew-tap and is updated automatically
# by the release pipeline in ulfbou/dx.cli via mislav/bump-homebrew-formula-action.
#
# Installation:
#   brew install ulfbou/tap/dxs
#
# Note: This formula currently targets Apple Silicon (osx-arm64).
# Intel Mac (osx-x64) multi-arch support is planned for a future release.
# Intel Mac users can install via:
#   curl -sSL https://raw.githubusercontent.com/ulfbou/dx.cli/main/install.sh | bash
# or:
#   dotnet tool install -g dxs

class Dxs < Formula
  desc "Transactional CLI for deterministic workspace mutation, snapshotting, and differential execution"
  homepage "https://github.com/ulfbou/dx.cli"

  # The release pipeline updates this URL and sha256 automatically on each stable release.
  url "https://github.com/ulfbou/dx.cli/releases/download/v0.2.0/dxs-0.2.0-osx-arm64.tar.gz"
  sha256 "0000000000000000000000000000000000000000000000000000000000000000"

  license "MIT"

  # dxs is a self-contained binary — no .NET runtime required.
  # No dependencies are declared.

  def install
    bin.install "dxs"
  end

  test do
    # Verify the binary runs and reports the expected version.
    assert_match version.to_s, shell_output("#{bin}/dxs --version")
  end
end
