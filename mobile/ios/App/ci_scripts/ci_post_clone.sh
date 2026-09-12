#!/bin/sh
set -eu

: "${CI_PRIMARY_REPOSITORY_PATH:?請由 Xcode Cloud 執行此腳本}"

export HOMEBREW_NO_AUTO_UPDATE=1
export HOMEBREW_NO_INSTALL_CLEANUP=1
brew install node@24
export PATH="$(brew --prefix node@24)/bin:$PATH"

cd "$CI_PRIMARY_REPOSITORY_PATH/mobile"
npm ci
npm test
npm run ios:sync
