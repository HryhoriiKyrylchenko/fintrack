namespace FinTrack.Budget.Contracts.V1.Buckets;

/// <summary>
/// Body of <c>POST /api/v1/buckets/{id}/rename</c>. Answers 204; renaming to the current name changes
/// nothing. Fails with <c>bucket.not_found</c> (404), <c>bucket.system_bucket</c>, <c>bucket.archived</c>
/// or <c>bucket.name_taken</c> (422).
/// </summary>
/// <param name="Name">The new name, with the rules of <see cref="CreateBucketRequest.Name"/>.</param>
public sealed record RenameBucketRequest(string Name);
