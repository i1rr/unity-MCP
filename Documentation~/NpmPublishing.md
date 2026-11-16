# Previous npm publishing

Public npm publishing stopped after 1.0.1. Follow
[Unity registry publishing](RegistryPublishing.md) for new releases.

Unity dependencies and npm dependencies share manifest field names but use
different package sources. Public npm scanners resolved Unity's dependency IDs
as unrelated npm packages with malware advisories. See [security](../SECURITY.md).
Keep existing npm versions immutable; do not replace them or hide dependencies.

The npm account is only needed to maintain the old npm listing. Consumers of the
Unity registry need no account. `Tools~/publish_npm.py` retains archive comparison
and local-registry testing, but refuses publication to public npm.
