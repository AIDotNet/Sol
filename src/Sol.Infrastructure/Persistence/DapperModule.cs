// Enables Dapper.AOT interceptor generation for every Dapper call site in this assembly.
//
// This attribute, the Dapper.AOT package reference, and the InterceptorsPreviewNamespaces
// property must all live in the same project as the call sites: interceptors are a compile-time
// artefact and do not flow across a ProjectReference. A Dapper call made from another project
// compiles against reflection-based Dapper instead, with no build error, and throws only when
// running as a native binary.
[module: Dapper.DapperAot]
