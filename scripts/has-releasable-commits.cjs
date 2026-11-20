'use strict'

// Decides whether there is anything worth releasing since the latest tag, so the
// release can be skipped instead of producing an empty patch version.
//
// Used by .github/workflows/release.yml before standard-version runs.
//
//   node scripts/has-releasable-commits.cjs <since-tag>   -> exit 0: releasable, 1: no
//   node scripts/has-releasable-commits.cjs               -> same thing against the whole history
//
// Types that trigger a release: feat/feature -> minor, fix/perf/revert/build/refactor -> patch,
// BREAKING CHANGE or ! -> major. Types such as chore, docs, ci, style and test do not trigger a release.

const { execFileSync } = require('child_process')

const RELEASE_TYPES = new Set(['feat', 'feature', 'fix', 'perf', 'revert', 'build', 'refactor'])

const HEADER = /^(?<type>[a-zA-Z]+)(?:\([^)]*\))?(?<bang>!)?:\s/

function parseCommits (raw) {
  const blocks = raw.split('===COMMIT===').map(block => block.trim())
  const commits = []

  for (const block of blocks) {
    const lines = block.split('\n')
    const subject = (lines.find(line => line.trim()) || '').trim()
    if (!subject) continue

    const match = HEADER.exec(subject)
    commits.push({
      type: match ? match.groups.type.toLowerCase() : null,
      bang: Boolean(match && match.groups.bang),
      breaking: /^BREAKING[ -]CHANGE/m.test(block)
    })
  }

  return commits
}

function main () {
  const from = process.argv[2]
  const range = from ? `${from}..HEAD` : 'HEAD'

  const raw = execFileSync(
    'git',
    ['log', '--format=%B%n%n===COMMIT===', range],
    { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }
  )

  const commits = parseCommits(raw).filter(c => c.type !== null)
  const releasable = commits.filter(
    c => c.breaking || c.bang || RELEASE_TYPES.has(c.type)
  )

  const since = from || 'repo start'

  if (releasable.length === 0) {
    console.log(`No releasable commits since ${since} (${commits.length} conventional commits found).`)
    return 1
  }

  console.log(`${releasable.length} releasable commits since ${since}:`)
  for (const commit of releasable) {
    console.log(`  ${commit.bang || commit.breaking ? '!' : ' '} ${commit.type}`)
  }
  return 0
}

try {
  process.exitCode = main()
} catch (err) {
  console.error(`Could not determine whether there are releasable commits: ${err.message}`)
  process.exitCode = 2
}
