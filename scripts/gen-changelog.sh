#!/usr/bin/env bash
# 从 git 提交历史机械生成 CHANGELOG.md：按月分组，组内按 conventional commit 类型标注。
# 本仓库提交信息遵循 conventional commits，且在单行内写明根因/影响/验证，
# 故 CHANGELOG 不手写、直接从 git log 派生。重新生成：scripts/gen-changelog.sh
set -euo pipefail
cd "$(dirname "$0")/.."

label() {
  case "$1" in
    feat) echo "新增" ;;
    fix) echo "修复" ;;
    perf) echo "性能" ;;
    refactor) echo "重构" ;;
    docs) echo "文档" ;;
    test) echo "测试" ;;
    chore | build | ci) echo "工程" ;;
    *) echo "其他" ;;
  esac
}

{
  echo "# Changelog"
  echo
  echo "由 \`scripts/gen-changelog.sh\` 从 git 提交历史机械生成——本仓库提交信息遵循"
  echo "conventional commits，并在单行内写明根因、影响面与验证方式，故不另维护手写条目。"
  echo "发版流程：打 \`vX.Y.Z\` tag（MinVer 自动派生构建版本）。"
  echo
  prev_month=""
  git log --date=format:'%Y-%m' --pretty='%ad%x09%s' | while IFS=$'\t' read -r month subject; do
    if [ "$month" != "$prev_month" ]; then
      echo
      echo "## $month"
      echo
      prev_month="$month"
    fi
    type="${subject%%:*}"
    rest="${subject#*: }"
    # 剥离 scope：feat(models): xxx → feat / xxx
    case "$type" in
    *\(*\)) type="${type%%(*}" ;;
    esac
    echo "- **[$(label "$type")]** $rest"
  done
} > CHANGELOG.md

echo "CHANGELOG.md 已生成：$(grep -c '^- ' CHANGELOG.md) 条提交，$(grep -c '^## ' CHANGELOG.md) 个月份分组"
