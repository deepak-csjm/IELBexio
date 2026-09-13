# Extra CA certificates for the container build

Drop any additional root CA certificates (`*.crt`, PEM-encoded) into this directory and they are
installed into the **build stage** before `dotnet restore` runs.

This exists for one specific, common situation: an organisation that terminates and re-signs outbound
TLS. In that case `api.nuget.org` presents a certificate signed by the organisation's own root, the
build container does not trust it, and `dotnet restore` fails with `NU1301 ... UntrustedRoot`.

The directory is empty by default, so the image builds normally with direct internet access. Nothing
here reaches the runtime image.

Do not put private keys here. Root certificates are public by nature; anything else does not belong.
