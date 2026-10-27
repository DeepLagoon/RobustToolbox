# Engine changes and official-client compatibility

- This repository owns RobustToolbox engine changes. Keep gameplay and content
  changes in Station. Before editing, identify the Station engine pin, the
  upstream engine commit and the official client/module versions that the
  intended release must support.
- Do not change client-side engine behavior on which compatibility with the
  official launcher or hub depends. This includes module loading and version
  selection, signatures and trust checks, build identity, connection and
  authentication protocols, and client/server wire contracts. Never bypass an
  upstream `insecure` designation or substitute an unsigned local WebView for
  the official signed module. If a requested fix requires such a change, stop
  that implementation and explain the compatibility constraint.
- Keep the client sandbox effective. Do not weaken `Sandbox.yml`,
  `AssemblyTypeChecker`, IL verification or module-loading restrictions to make
  content compile or load. Diagnose the rejected API/IL and adapt the content
  code or use an allowed engine API without expanding client privileges.
- Review shared-engine changes for effects on client serialization, network
  messages and sandbox-visible APIs even when the edited code is not under
  `Robust.Client`. Validate affected engine tests and the Station client
  sandbox/type checks against rebuilt assemblies. Compilation or a local
  client launch does not prove official-client compatibility.
- For an engine upgrade, preserve a traceable upstream commit and compare any
  fork-specific changes against it. Validate the packaged release with the
  official client and a real TGUI window before claiming that the migration
  works. Keep unsupported or unverified runtime behavior out of the success
  report.
