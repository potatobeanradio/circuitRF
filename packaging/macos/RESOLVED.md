# packaging/macos — resolved findings

## Notary profile: "not stored" when it was, and lost mid-build (2026-09-23)

Two separate faults, both seen in one release run on macOS 27 with notarytool 1.1.3.

- **The start-up check could not see the profile.** It looked the profile up with
  `security find-generic-password`. notarytool keeps the profile in the data-protection keychain,
  and `security` cannot see that keychain. So the check reported "no notary credentials" for a
  profile notarytool was reading correctly (`notarytool history --keychain-profile …` succeeded at
  the same moment), and the build asked for credentials that were already stored. The check now
  asks notarytool itself, with `notarytool history`. It tells a missing profile ("No Keychain
  password item found") apart from any other refusal and prints the refusal.
- **A locked screen breaks the NEXT submission, not the current one.** The data-protection keychain
  can't be read while the session is locked, and every `notarytool submit` reads the profile again.
  The `.app` submission read the profile, then the screen locked during its wait; it was still
  Accepted. The `.dmg` submission a few minutes later failed with "No Keychain password item
  found", which looks like the credentials were deleted. Pass 2 now runs under
  `caffeinate -di -w $$`, which stops the screen from locking on idle. `crf_notarise` names a
  keychain that was locked during the run as the cause, instead of pointing at `notarytool log`,
  which has no submission to show.
