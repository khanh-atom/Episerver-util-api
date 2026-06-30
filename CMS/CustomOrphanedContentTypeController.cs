using EPiServer.Security;

namespace Foundation.Custom.Episerver_util_api.CMS
{
    /// <summary>
    /// Diagnostic API for reproducing orphaned content type failures where a saved CMS content type points to a missing .NET model class.
    /// </summary>
    [ApiController]
    [Route("util-api/custom-orphaned-content-type")]
    public class CustomOrphanedContentTypeController : ControllerBase
    {
        private const string BaseUrl = "https://localhost:5009";

        private readonly IContentTypeRepository _contentTypeRepository;
        private readonly IContentModelUsage _contentModelUsage;
        private readonly IContentRepository _contentRepository;

        public CustomOrphanedContentTypeController(
            IContentTypeRepository contentTypeRepository,
            IContentModelUsage contentModelUsage,
            IContentRepository contentRepository)
        {
            _contentTypeRepository = contentTypeRepository;
            _contentModelUsage = contentModelUsage;
            _contentRepository = contentRepository;
        }

        /// <summary>
        /// Step 1: Shows the reproduction flow and browser-friendly sample URLs.
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/summary
        /// </summary>
        [HttpGet("summary")]
        public IActionResult Summary()
        {
            try
            {
                var firstInvalidType = FindInvalidModelContentTypes().FirstOrDefault();
                var firstUsage = firstInvalidType == null
                    ? null
                    : _contentModelUsage.ListContentOfContentType(firstInvalidType).FirstOrDefault();

                return Ok(new
                {
                    Purpose = "Use real CMS APIs to reproduce an orphaned content type whose ModelTypeString no longer resolves to a .NET type.",
                    Steps = new[]
                    {
                        "Step 1: GET /util-api/custom-orphaned-content-type/summary",
                        "Step 2: GET /util-api/custom-orphaned-content-type/step2-find-invalid-content-types",
                        "Step 3: GET /util-api/custom-orphaned-content-type/step3-inspect-content-type?contentTypeGuid=00000000-0000-0000-0000-000000000000",
                        "Step 4: GET /util-api/custom-orphaned-content-type/step4-list-content-usages?contentTypeGuid=00000000-0000-0000-0000-000000000000",
                        "Step 5: GET /util-api/custom-orphaned-content-type/step5-load-content-usage?contentId=123",
                        "Step 6: GET /util-api/custom-orphaned-content-type/step6-attempt-delete-content-type?contentTypeGuid=00000000-0000-0000-0000-000000000000",
                        "Optional cleanup: GET /util-api/custom-orphaned-content-type/step7-force-delete-content-usages?contentTypeGuid=00000000-0000-0000-0000-000000000000&confirmDelete=true"
                    },
                    SampleUrls = new
                    {
                        Summary = $"{BaseUrl}/util-api/custom-orphaned-content-type/summary",
                        FindInvalidContentTypes = $"{BaseUrl}/util-api/custom-orphaned-content-type/step2-find-invalid-content-types",
                        InspectContentType = firstInvalidType == null
                            ? $"{BaseUrl}/util-api/custom-orphaned-content-type/step3-inspect-content-type?contentTypeGuid=00000000-0000-0000-0000-000000000000"
                            : $"{BaseUrl}/util-api/custom-orphaned-content-type/step3-inspect-content-type?contentTypeGuid={firstInvalidType.GUID}",
                        ListContentUsages = firstInvalidType == null
                            ? $"{BaseUrl}/util-api/custom-orphaned-content-type/step4-list-content-usages?contentTypeGuid=00000000-0000-0000-0000-000000000000"
                            : $"{BaseUrl}/util-api/custom-orphaned-content-type/step4-list-content-usages?contentTypeGuid={firstInvalidType.GUID}",
                        LoadContentUsage = firstUsage == null
                            ? $"{BaseUrl}/util-api/custom-orphaned-content-type/step5-load-content-usage?contentId=123"
                            : $"{BaseUrl}/util-api/custom-orphaned-content-type/step5-load-content-usage?contentId={firstUsage.ContentLink.ID}",
                        AttemptDeleteContentType = firstInvalidType == null
                            ? $"{BaseUrl}/util-api/custom-orphaned-content-type/step6-attempt-delete-content-type?contentTypeGuid=00000000-0000-0000-0000-000000000000"
                            : $"{BaseUrl}/util-api/custom-orphaned-content-type/step6-attempt-delete-content-type?contentTypeGuid={firstInvalidType.GUID}",
                        ForceDeleteContentUsages = firstInvalidType == null
                            ? $"{BaseUrl}/util-api/custom-orphaned-content-type/step7-force-delete-content-usages?contentTypeGuid=00000000-0000-0000-0000-000000000000&confirmDelete=true"
                            : $"{BaseUrl}/util-api/custom-orphaned-content-type/step7-force-delete-content-usages?contentTypeGuid={firstInvalidType.GUID}&confirmDelete=true"
                    },
                    FirstDetectedInvalidContentType = GetContentTypeSnapshot(firstInvalidType),
                    FirstDetectedUsage = GetContentUsageSnapshot(firstUsage),
                    Notes = new[]
                    {
                        "If no query value is supplied, the endpoints try to use the first content type where ModelTypeString is set but ModelType cannot be resolved.",
                        "The delete-content-type step calls IContentTypeRepository.Delete for real. It should fail when the content type is still used.",
                        "The force-delete step calls IContentRepository.Delete for real and is intended only for disposable or restored databases."
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Finds content types whose ModelTypeString is saved in the database but no longer resolves to a loaded .NET type.
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step2-find-invalid-content-types
        /// </summary>
        [HttpGet("step2-find-invalid-content-types")]
        public IActionResult FindInvalidContentTypes()
        {
            try
            {
                var invalidTypes = FindInvalidModelContentTypes()
                    .Select(GetContentTypeSnapshot)
                    .ToList();

                return Ok(new
                {
                    Count = invalidTypes.Count,
                    Items = invalidTypes,
                    Interpretation = invalidTypes.Any()
                        ? "These content types have a ModelTypeString value that does not resolve to a .NET class in the running application."
                        : "No content types with an unresolved ModelTypeString were found in this database."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Inspects one content type by GUID, ID, name, or the first detected invalid content type.
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step3-inspect-content-type?contentTypeGuid=00000000-0000-0000-0000-000000000000
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step3-inspect-content-type?contentTypeName=SampleContentType
        /// </summary>
        [HttpGet("step3-inspect-content-type")]
        public IActionResult InspectContentType(Guid? contentTypeGuid = null, int? contentTypeId = null, string contentTypeName = null)
        {
            try
            {
                var contentType = ResolveContentType(contentTypeGuid, contentTypeId, contentTypeName);
                if (contentType == null)
                {
                    return NotFound(new
                    {
                        Found = false,
                        Message = "No matching content type was found. Provide contentTypeGuid, contentTypeId, or contentTypeName, or run step2 to discover invalid content types."
                    });
                }

                var usages = _contentModelUsage.ListContentOfContentType(contentType);

                return Ok(new
                {
                    Found = true,
                    ContentType = GetContentTypeSnapshot(contentType),
                    IsUsed = usages.Any(),
                    UsageCount = usages.Count,
                    FirstUsage = GetContentUsageSnapshot(usages.FirstOrDefault()),
                    NextUrls = new
                    {
                        ListUsages = $"{BaseUrl}/util-api/custom-orphaned-content-type/step4-list-content-usages?contentTypeGuid={contentType.GUID}",
                        AttemptDelete = $"{BaseUrl}/util-api/custom-orphaned-content-type/step6-attempt-delete-content-type?contentTypeGuid={contentType.GUID}"
                    }
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Lists real content usages for the selected content type without loading the content item models.
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step4-list-content-usages?contentTypeGuid=00000000-0000-0000-0000-000000000000
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step4-list-content-usages?contentTypeName=SampleContentType
        /// </summary>
        [HttpGet("step4-list-content-usages")]
        public IActionResult ListContentUsages(Guid? contentTypeGuid = null, int? contentTypeId = null, string contentTypeName = null)
        {
            try
            {
                var contentType = ResolveContentType(contentTypeGuid, contentTypeId, contentTypeName);
                if (contentType == null)
                {
                    return NotFound(new
                    {
                        Found = false,
                        Message = "No matching content type was found. Provide contentTypeGuid, contentTypeId, or contentTypeName, or run step2 to discover invalid content types."
                    });
                }

                var rawUsages = _contentModelUsage.ListContentOfContentType(contentType)
                    .OrderBy(x => x.ContentLink?.ID ?? 0)
                    .ThenBy(x => x.LanguageBranch)
                    .ToList();
                var usages = rawUsages.Select(GetContentUsageSnapshot).ToList();
                var firstUsageId = rawUsages.FirstOrDefault()?.ContentLink?.ID;

                return Ok(new
                {
                    ContentType = GetContentTypeSnapshot(contentType),
                    UsageCount = usages.Count,
                    IsContentTypeUsed = usages.Any(),
                    Usages = usages,
                    NextUrl = firstUsageId.HasValue
                        ? $"{BaseUrl}/util-api/custom-orphaned-content-type/step5-load-content-usage?contentId={firstUsageId.Value}"
                        : null,
                    Interpretation = usages.Any()
                        ? "These usages are what block IContentTypeRepository.Delete from deleting the content type."
                        : "This content type has no content usages according to IContentModelUsage."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Loads a real content item through IContentRepository.Get to reproduce the ContentFactory failure for invalid model types.
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step5-load-content-usage?contentId=123
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step5-load-content-usage?contentTypeGuid=00000000-0000-0000-0000-000000000000
        /// </summary>
        [HttpGet("step5-load-content-usage")]
        public IActionResult LoadContentUsage(int? contentId = null, Guid? contentTypeGuid = null, int? contentTypeId = null, string contentTypeName = null)
        {
            try
            {
                var contentLink = contentId.HasValue
                    ? new ContentReference(contentId.Value)
                    : ResolveFirstUsageContentLink(contentTypeGuid, contentTypeId, contentTypeName);

                if (ContentReference.IsNullOrEmpty(contentLink))
                {
                    return NotFound(new
                    {
                        Found = false,
                        Message = "No content ID was supplied and no usage was found for the selected content type."
                    });
                }

                var content = _contentRepository.Get<IContent>(contentLink.ToReferenceWithoutVersion());

                return Ok(new
                {
                    Loaded = true,
                    Content = GetContentSnapshot(content),
                    Interpretation = "The content item loaded successfully. If you expected the orphaned model failure, verify that this content item still points to the invalid content type."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Calls IContentTypeRepository.Delete for real to reproduce the delete blocker when usages still exist.
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step6-attempt-delete-content-type?contentTypeGuid=00000000-0000-0000-0000-000000000000
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step6-attempt-delete-content-type?contentTypeName=SampleContentType
        /// </summary>
        [HttpGet("step6-attempt-delete-content-type")]
        public IActionResult AttemptDeleteContentType(Guid? contentTypeGuid = null, int? contentTypeId = null, string contentTypeName = null)
        {
            try
            {
                var contentType = ResolveContentType(contentTypeGuid, contentTypeId, contentTypeName);
                if (contentType == null)
                {
                    return NotFound(new
                    {
                        Found = false,
                        Message = "No matching content type was found. Provide contentTypeGuid, contentTypeId, or contentTypeName, or run step2 to discover invalid content types."
                    });
                }

                var beforeDelete = GetContentTypeSnapshot(contentType);
                var usagesBeforeDelete = _contentModelUsage.ListContentOfContentType(contentType)
                    .Select(GetContentUsageSnapshot)
                    .ToList();

                _contentTypeRepository.Delete(contentType);

                return Ok(new
                {
                    Deleted = true,
                    ContentTypeBeforeDelete = beforeDelete,
                    UsageCountBeforeDelete = usagesBeforeDelete.Count,
                    UsagesBeforeDelete = usagesBeforeDelete,
                    Interpretation = "The content type was deleted. In the orphaned-type scenario, this call usually throws while usages still exist."
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 7: Force-deletes real content usages for the selected content type using IContentRepository.Delete.
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step7-force-delete-content-usages?contentTypeGuid=00000000-0000-0000-0000-000000000000&amp;confirmDelete=true
        /// Sample usage: https://localhost:5009/util-api/custom-orphaned-content-type/step7-force-delete-content-usages?contentTypeName=SampleContentType&amp;confirmDelete=true
        /// </summary>
        [HttpGet("step7-force-delete-content-usages")]
        public IActionResult ForceDeleteContentUsages(Guid? contentTypeGuid = null, int? contentTypeId = null, string contentTypeName = null, bool confirmDelete = false, int maxItems = 100)
        {
            try
            {
                var contentType = ResolveContentType(contentTypeGuid, contentTypeId, contentTypeName);
                if (contentType == null)
                {
                    return NotFound(new
                    {
                        Found = false,
                        Message = "No matching content type was found. Provide contentTypeGuid, contentTypeId, or contentTypeName, or run step2 to discover invalid content types."
                    });
                }

                var usages = _contentModelUsage.ListContentOfContentType(contentType)
                    .OrderBy(x => x.ContentLink?.ID ?? 0)
                    .ThenBy(x => x.LanguageBranch)
                    .ToList();

                if (!confirmDelete)
                {
                    return Ok(new
                    {
                        Executed = false,
                        ContentType = GetContentTypeSnapshot(contentType),
                        UsageCount = usages.Count,
                        RequiredQuery = "Add confirmDelete=true to run the real force-delete operation.",
                        SampleUrl = $"{BaseUrl}/util-api/custom-orphaned-content-type/step7-force-delete-content-usages?contentTypeGuid={contentType.GUID}&confirmDelete=true",
                        Warning = "This endpoint permanently deletes content usages for the selected content type."
                    });
                }

                var sanitizedMaxItems = Math.Max(1, maxItems);
                var deleted = new List<object>();

                foreach (var usage in usages.Take(sanitizedMaxItems))
                {
                    var contentLink = usage.ContentLink.ToReferenceWithoutVersion();
                    _contentRepository.Delete(contentLink, true, AccessLevel.NoAccess);
                    deleted.Add(GetContentUsageSnapshot(usage));
                }

                var remainingUsages = _contentModelUsage.ListContentOfContentType(contentType)
                    .Select(GetContentUsageSnapshot)
                    .ToList();

                return Ok(new
                {
                    Executed = true,
                    ContentType = GetContentTypeSnapshot(contentType),
                    DeletedCount = deleted.Count,
                    DeletedUsages = deleted,
                    RemainingUsageCount = remainingUsages.Count,
                    RemainingUsages = remainingUsages,
                    NextUrl = remainingUsages.Any()
                        ? $"{BaseUrl}/util-api/custom-orphaned-content-type/step7-force-delete-content-usages?contentTypeGuid={contentType.GUID}&confirmDelete=true"
                        : $"{BaseUrl}/util-api/custom-orphaned-content-type/step6-attempt-delete-content-type?contentTypeGuid={contentType.GUID}"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        private List<ContentType> FindInvalidModelContentTypes()
        {
            return _contentTypeRepository.List()
                .Where(HasInvalidModelTypeReference)
                .OrderBy(x => x.Name)
                .ToList();
        }

        private ContentType ResolveContentType(Guid? contentTypeGuid, int? contentTypeId, string contentTypeName)
        {
            if (contentTypeGuid.HasValue && contentTypeGuid.Value != Guid.Empty)
            {
                return _contentTypeRepository.Load(contentTypeGuid.Value);
            }

            if (contentTypeId.HasValue && contentTypeId.Value > 0)
            {
                return _contentTypeRepository.Load(contentTypeId.Value);
            }

            if (!string.IsNullOrWhiteSpace(contentTypeName))
            {
                var exactNameMatch = _contentTypeRepository.Load(contentTypeName);
                if (exactNameMatch != null)
                {
                    return exactNameMatch;
                }

                return _contentTypeRepository.List()
                    .FirstOrDefault(x =>
                        string.Equals(x.Name, contentTypeName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.DisplayName, contentTypeName, StringComparison.OrdinalIgnoreCase));
            }

            return FindInvalidModelContentTypes().FirstOrDefault();
        }

        private ContentReference ResolveFirstUsageContentLink(Guid? contentTypeGuid, int? contentTypeId, string contentTypeName)
        {
            var contentType = ResolveContentType(contentTypeGuid, contentTypeId, contentTypeName);
            return contentType == null
                ? null
                : _contentModelUsage.ListContentOfContentType(contentType)
                    .OrderBy(x => x.ContentLink?.ID ?? 0)
                    .FirstOrDefault()
                    ?.ContentLink;
        }

        private static bool HasInvalidModelTypeReference(ContentType contentType)
        {
            return !string.IsNullOrWhiteSpace(contentType.ModelTypeString) && contentType.ModelType == null;
        }

        private static object GetContentTypeSnapshot(ContentType contentType)
        {
            if (contentType == null)
            {
                return null;
            }

            var modelType = contentType.ModelType;
            var hasModelTypeString = !string.IsNullOrWhiteSpace(contentType.ModelTypeString);

            return new
            {
                contentType.ID,
                Guid = contentType.GUID,
                contentType.Name,
                contentType.DisplayName,
                contentType.GroupName,
                Base = contentType.Base.ToString(),
                contentType.Description,
                contentType.IsAvailable,
                contentType.SortOrder,
                contentType.ModelTypeString,
                ModelTypeFullName = modelType?.FullName,
                ModelTypeResolved = modelType != null,
                HasModelTypeString = hasModelTypeString,
                InvalidModelTypeReference = hasModelTypeString && modelType == null,
                PropertyDefinitionCount = contentType.PropertyDefinitions?.Count ?? 0
            };
        }

        private static object GetContentUsageSnapshot(ContentUsage usage)
        {
            if (usage == null)
            {
                return null;
            }

            return new
            {
                usage.Name,
                usage.LanguageBranch,
                ContentLink = GetContentReferenceSnapshot(usage.ContentLink),
                LoadUrl = usage.ContentLink == null
                    ? null
                    : $"{BaseUrl}/util-api/custom-orphaned-content-type/step5-load-content-usage?contentId={usage.ContentLink.ID}"
            };
        }

        private static object GetContentSnapshot(IContent content)
        {
            if (content == null)
            {
                return null;
            }

            return new
            {
                content.Name,
                content.ContentGuid,
                ContentLink = GetContentReferenceSnapshot(content.ContentLink),
                RuntimeType = content.GetType().FullName,
                OriginalType = content.GetOriginalType().FullName,
                Status = content is IVersionable versionable ? versionable.Status.ToString() : null
            };
        }

        private static object GetContentReferenceSnapshot(ContentReference contentLink)
        {
            if (ContentReference.IsNullOrEmpty(contentLink))
            {
                return null;
            }

            var withoutVersion = contentLink.ToReferenceWithoutVersion();

            return new
            {
                Id = contentLink.ID,
                WorkId = contentLink.WorkID,
                contentLink.ProviderName,
                Value = contentLink.ToString(),
                WithoutVersion = withoutVersion.ToString()
            };
        }
    }
}
