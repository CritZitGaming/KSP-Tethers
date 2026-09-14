# CKAN metadata

**KSP Tethers has been submitted to CKAN and is waiting for review.** The submission is
[KSP-CKAN/NetKAN#11554](https://github.com/KSP-CKAN/NetKAN/pull/11554), opened on 2026-09-14; its automated
inflate check passed (version 1.0.0, KSP 1.12.0 to 1.12.99, MIT). Until it is merged, install manually from the
[Releases](https://github.com/CritZitGaming/KSP-Tethers/releases) page. When it is merged, remove the pending
notice from the main README.

## Where the real metadata lives

The file CKAN actually reads is `NetKAN/KSPTethers.netkan` in <https://github.com/KSP-CKAN/NetKAN>, submitted
there as a pull request. **Editing `KSPTethers.netkan` in this repo changes nothing on CKAN**: changes go in as
a pull request against that repository, and contributions there are made under CC-0 (the metadata only, not the
mod). The copy here records what was submitted.

The definition follows the shape the CKAN maintainers settled on for this author's other mods, which decides
what each release has to get right:

| Field | Reads from | Must stay in sync with |
|---|---|---|
| `$kref` `#/ckan/github/...` | the latest GitHub release and its attached zip | the workflow attaching exactly one zip |
| `x_netkan_version_edit` | the release's git tag, with a leading `v` stripped | `KSPTethers/KSPTethers.version` |
| `$vref` `ksp-avc` | `KSPTethers/KSPTethers.version` inside the zip | the tag, and where the zip puts the folder |
| *(no `install` stanza)* | CKAN's default: the directory named after the identifier | the mod's folder staying `KSPTethers` |

The version therefore comes from the **tag**, not the zip filename. The release workflow fails the build if the
tag, `KSPTethers.version`, the project version and the committed DLL disagree.

The GitHub repository's description becomes the CKAN abstract, and its website field (on the repo's About
panel) becomes the CKAN homepage, so a forum thread can be linked later without touching the metadata.

## Releasing

Push a `v<version>` tag. The `Release` workflow in `.github/workflows/` builds the zip with `KSPTethers/` at its
root and attaches it, and once the listing is live CKAN picks each release up within a few hours. Nothing on
the CKAN side needs touching.
