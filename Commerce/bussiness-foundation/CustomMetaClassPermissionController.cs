using EPiServer.Authorization;
using EPiServer.DataAbstraction;
using EPiServer.Security;
using EPiServer.ServiceLocation;
using Mediachase.BusinessFoundation.Data;
using Mediachase.BusinessFoundation.Data.Business;
using Mediachase.BusinessFoundation.Data.Meta;
using Mediachase.BusinessFoundation.Data.Meta.Management;
using Mediachase.Commerce.Customers;
using Mediachase.Commerce.Security;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

namespace Foundation.Custom.Episerver_util_api.Commerce.BusinessFoundation
{
    /// <summary>
    /// Demonstrates that AddPermissions() registers per-metaclass permissions but the Commerce 14 UI 
    /// does not check them when rendering custom metaclass relation tabs on Contact/Organization pages.
    /// 
    /// Built-in Contact/Organization permissions ARE enforced, but custom metaclass permissions are not.
    /// </summary>
    [ApiController]
    [Route("util-api/custom-meta-class-permission")]
    public class CustomMetaClassPermissionController : ControllerBase
    {
        private const string TestClassName = "TestPermObj";
        private const string TestFriendlyName = "Test Permission Object";
        private const string EPiCommerce = "EPiCommerce";

        private readonly PermissionRepository _permissionRepository;
        private readonly PermissionService _permissionService;

        public CustomMetaClassPermissionController(
            PermissionRepository permissionRepository,
            PermissionService permissionService)
        {
            _permissionRepository = permissionRepository;
            _permissionService = permissionService;
        }

        /// <summary>
        /// Step 1: Create a custom metaclass with a 1:N reference to Organization, then call AddPermissions().
        /// This is what a partner would typically do to add a custom BF object to Organization.
        /// Sample usage: https://localhost:5000/util-api/custom-meta-class-permission/create-custom-class
        /// </summary>
        [HttpGet("create-custom-class")]
        public IActionResult CreateCustomClass()
        {
            try
            {
                using (var scope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, Mediachase.BusinessFoundation.Data.Meta.Management.AccessLevel.System))
                {
                    var manager = DataContext.Current.MetaModel;
                    var metaClass = manager.MetaClasses[TestClassName];

                    if (metaClass == null)
                    {
                        metaClass = manager.CreateMetaClass(
                            TestClassName, TestFriendlyName, TestClassName,
                            $"cls_{TestClassName}", PrimaryKeyIdValueType.Guid);
                    }

                    // Create fields at Customization level so they show in UI
                    using (var builder = new MetaFieldBuilder(metaClass))
                    {
                        if (!metaClass.Fields.Contains("Title"))
                        {
                            builder.CreateText("Title", "Title", true, 256, false, false);
                        }

                        if (!metaClass.Fields.Contains("Description"))
                        {
                            builder.CreateText("Description", "Description", true, 512, false, false);
                        }

                        builder.SaveChanges();
                    }

                    // Set title field
                    metaClass.TitleFieldName = "Title";

                    // Add 1:N reference to Organization
                    if (!metaClass.Fields.Contains("OrganizationLink"))
                    {
                        using (var builder = new MetaFieldBuilder(metaClass))
                        {
                            builder.CreateReference("OrganizationLink", "Organization Link",
                                true, OrganizationEntity.ClassName, true);
                            builder.SaveChanges();
                        }
                    }

                    // Fix field access levels to Customization
                    foreach (var fieldName in new[] { "Title", "Description" })
                    {
                        if (metaClass.Fields.Contains(fieldName))
                        {
                            metaClass.Fields[fieldName].AccessLevel = Mediachase.BusinessFoundation.Data.Meta.Management.AccessLevel.Customization;
                        }
                    }

                    // This is the key call — registers permissions in CMS PermissionRepository
                    metaClass.AddPermissions();

                    scope.SaveChanges();

                    var fieldDetails = metaClass.Fields.Cast<MetaField>().Select(f => new
                    {
                        name = f.Name,
                        friendlyName = f.FriendlyName,
                        accessLevel = f.AccessLevel.ToString(),
                        typeName = f.TypeName
                    }).ToList();

                    return Ok(new
                    {
                        success = true,
                        message = $"Custom metaclass '{TestClassName}' created with 1:N reference to Organization and AddPermissions() called",
                        metaClassName = metaClass.Name,
                        titleFieldName = metaClass.TitleFieldName,
                        fields = fieldDetails,
                        permissionsRegistered = new[]
                        {
                            $"businessfoundation:{TestClassName.ToLower()}:list:permission",
                            $"businessfoundation:{TestClassName.ToLower()}:create:permission",
                            $"businessfoundation:{TestClassName.ToLower()}:edit:permission",
                            $"businessfoundation:{TestClassName.ToLower()}:delete:permission",
                            $"businessfoundation:{TestClassName.ToLower()}:view:permission"
                        },
                        nextStep = "Call /show-registered-permissions to verify the permissions exist in PermissionRepository"
                    });
                }
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 2: Show all permissions registered by AddPermissions() for the custom metaclass.
        /// Proves that permissions EXIST in the CMS PermissionRepository.
        /// Sample usage: https://localhost:5000/util-api/custom-meta-class-permission/show-registered-permissions
        /// </summary>
        [HttpGet("show-registered-permissions")]
        public IActionResult ShowRegisteredPermissions()
        {
            try
            {
                var permissionTypes = new[] { "list", "create", "edit", "delete", "view" };
                var registeredPermissions = new List<object>();

                foreach (var permission in permissionTypes)
                {
                    var permKey = $"businessfoundation:{TestClassName.ToLower()}:{permission}:permission";
                    var permissionType = new PermissionType(EPiCommerce, permKey);
                    var entities = _permissionRepository.GetPermissionsAsync(permissionType).Result;

                    registeredPermissions.Add(new
                    {
                        permissionKey = permKey,
                        group = EPiCommerce,
                        assignedTo = entities.Select(e => new
                        {
                            name = e.Name,
                            entityType = e.EntityType.ToString()
                        }).ToList(),
                        exists = entities.Any()
                    });
                }

                // Also show built-in Contact/Organization permissions for comparison
                var builtInPermissions = new List<object>();
                var builtInKeys = new[]
                {
                    CustomersPermissions.ContactViewPermission,
                    CustomersPermissions.ContactListPermission,
                    CustomersPermissions.OrgsViewPermission,
                    CustomersPermissions.OrgsListPermission,
                    CustomersPermissions.CustomerTabViewPermission,
                    CustomersPermissions.ContactTabViewPermission,
                    CustomersPermissions.OrgsTabViewPermission
                };

                foreach (var key in builtInKeys)
                {
                    var permissionType = new PermissionType(EPiCommerce, key);
                    var entities = _permissionRepository.GetPermissionsAsync(permissionType).Result;

                    builtInPermissions.Add(new
                    {
                        permissionKey = key,
                        assignedTo = entities.Select(e => new
                        {
                            name = e.Name,
                            entityType = e.EntityType.ToString()
                        }).ToList()
                    });
                }

                return Ok(new
                {
                    success = true,
                    message = "Permissions registered by AddPermissions() for custom metaclass",
                    customMetaClassPermissions = registeredPermissions,
                    builtInPermissions = builtInPermissions,
                    observation = "Both custom and built-in permissions exist in PermissionRepository, but only built-in ones are checked by the UI",
                    nextStep = "Call /check-current-user-permissions to see what the UI actually checks"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 3: Replicate what PermissionController.GetCurrentUserPermissions() returns to the React UI.
        /// Shows that ONLY built-in Contact/Organization permissions are sent — custom metaclass permissions are NOT included.
        /// Sample usage: https://localhost:5000/util-api/custom-meta-class-permission/check-current-user-permissions
        /// </summary>
        [HttpGet("check-current-user-permissions")]
        public IActionResult CheckCurrentUserPermissions()
        {
            try
            {
                var currentPrincipal = PrincipalInfo.CurrentPrincipal;
                var isAdmin = User.IsInRole(Roles.Administrators) || User.IsInRole(CommerceRoles.CommerceAdmins);

                // What the UI DOES check (from PermissionController.GetCurrentUserPermissions)
                var uiCheckedPermissions = new
                {
                    contactPermissions = new
                    {
                        list = _permissionService.IsPermitted(currentPrincipal, CustomersPermissions.ContactList),
                        view = _permissionService.IsPermitted(currentPrincipal, CustomersPermissions.ContactView),
                        create = _permissionService.IsPermitted(currentPrincipal, CustomersPermissions.ContactCreate),
                        edit = _permissionService.IsPermitted(currentPrincipal, CustomersPermissions.ContactEdit),
                        delete = _permissionService.IsPermitted(currentPrincipal, CustomersPermissions.ContactDelete)
                    },
                    orgsPermissions = new
                    {
                        list = _permissionService.IsPermitted(currentPrincipal, CustomersPermissions.OrgsList),
                        view = _permissionService.IsPermitted(currentPrincipal, CustomersPermissions.OrgsView),
                        create = _permissionService.IsPermitted(currentPrincipal, CustomersPermissions.OrgsCreate),
                        edit = _permissionService.IsPermitted(currentPrincipal, CustomersPermissions.OrgsEdit),
                        delete = _permissionService.IsPermitted(currentPrincipal, CustomersPermissions.OrgsDelete)
                    }
                };

                // What the UI does NOT check — custom metaclass permissions
                var permissionTypes = new[] { "list", "create", "edit", "delete", "view" };
                var customPermissions = new Dictionary<string, bool>();

                foreach (var permission in permissionTypes)
                {
                    var permKey = $"businessfoundation:{TestClassName.ToLower()}:{permission}:permission";
                    var permissionType = new PermissionType(EPiCommerce, permKey);
                    customPermissions[permission] = _permissionService.IsPermitted(currentPrincipal, permissionType);
                }

                return Ok(new
                {
                    success = true,
                    currentUser = User.Identity?.Name,
                    isAdmin,
                    userRoles = (User.Identity as ClaimsIdentity)?.Claims
                        .Where(c => c.Type == ClaimTypes.Role)
                        .Select(c => c.Value).ToList(),
                    whatUiChecks = new
                    {
                        description = "PermissionController.GetCurrentUserPermissions() sends ONLY these to the React UI",
                        permissions = uiCheckedPermissions
                    },
                    whatUiDoesNotCheck = new
                    {
                        description = $"Custom metaclass '{TestClassName}' permissions exist but are NEVER sent to or checked by the UI",
                        permissions = customPermissions,
                        wouldCurrentUserBeAllowed = customPermissions.All(p => p.Value)
                    },
                    gap = "The React UI receives contactPermissions and orgsPermissions but has no concept of per-custom-metaclass permissions",
                    nextStep = "Call /show-relation-tabs-without-filtering to see how tabs are returned without permission checks"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 4: Replicate the server-side tab discovery (GetRelation1NTabs / GetRelationNNTabs).
        /// Shows that tabs are returned for ALL custom metaclasses without any permission filtering.
        /// Sample usage: https://localhost:5000/util-api/custom-meta-class-permission/show-relation-tabs-without-filtering
        /// </summary>
        [HttpGet("show-relation-tabs-without-filtering")]
        public IActionResult ShowRelationTabsWithoutFiltering()
        {
            try
            {
                var systemClasses = new Dictionary<string, List<string>>
                {
                    { "Contact", new List<string> { "Address", "ContactNote", "CreditCard", "Organization" } },
                    { "Organization", new List<string> { "Contact", "Organization", "Address", "CreditCard" } },
                };

                var results = new Dictionary<string, object>();

                foreach (var parentClass in new[] { "Organization", "Contact" })
                {
                    var metaClass = DataContext.Current.MetaModel.MetaClasses[parentClass];
                    if (metaClass == null) continue;

                    // Replicate GetRelation1NTabs — NO permission check
                    var relation1NTabs = new List<object>();
                    foreach (MetaField field in metaClass.FindReferencesTo(true))
                    {
                        if (field.MetaClass.IsBridge || field.MetaClass.IsCard)
                            continue;

                        if (systemClasses.ContainsKey(parentClass)
                            && systemClasses[parentClass].Contains(field.MetaClass.Name)
                            && field.AccessLevel == Mediachase.BusinessFoundation.Data.Meta.Management.AccessLevel.System)
                            continue;

                        // Check if current user has custom metaclass permissions (but the real code doesn't do this!)
                        var viewPermKey = $"businessfoundation:{field.MetaClass.Name.ToLower()}:view:permission";
                        var viewPermType = new PermissionType(EPiCommerce, viewPermKey);
                        var hasViewPermission = _permissionService.IsPermitted(PrincipalInfo.CurrentPrincipal, viewPermType);

                        relation1NTabs.Add(new
                        {
                            refClassName = field.MetaClass.Name,
                            refFieldName = field.Name,
                            displayName = field.MetaClass.FriendlyName,
                            isCustomMetaClass = !systemClasses.ContainsKey(parentClass) || !systemClasses[parentClass].Contains(field.MetaClass.Name),
                            permissionCheck = new
                            {
                                permissionKey = viewPermKey,
                                userHasPermission = hasViewPermission,
                                isCheckedByUi = false,
                                wouldBeFilteredIfChecked = !hasViewPermission
                            }
                        });
                    }

                    // Replicate GetRelationNNTabs — NO permission check
                    var relationNNTabs = new List<object>();
                    foreach (var bridgeClass in DataContext.Current.MetaModel.GetBridgesReferencedToClass(metaClass))
                    {
                        if (!bridgeClass.Attributes.ContainsKey(MetaClassAttribute.BridgeRef1Name)
                            || !bridgeClass.Attributes.ContainsKey(MetaClassAttribute.BridgeRef2Name))
                            continue;

                        var bridgeFields = bridgeClass.GetBridgeFields();
                        var refClassName = parentClass == bridgeFields[0].ReferenceToMetaClassName
                            ? bridgeFields[1].ReferenceToMetaClassName
                            : bridgeFields[0].ReferenceToMetaClassName;

                        var viewPermKey = $"businessfoundation:{refClassName.ToLower()}:view:permission";
                        var viewPermType = new PermissionType(EPiCommerce, viewPermKey);
                        var hasViewPermission = _permissionService.IsPermitted(PrincipalInfo.CurrentPrincipal, viewPermType);

                        relationNNTabs.Add(new
                        {
                            bridgeClassName = bridgeClass.Name,
                            refClassName,
                            displayName = bridgeClass.FriendlyName,
                            permissionCheck = new
                            {
                                permissionKey = viewPermKey,
                                userHasPermission = hasViewPermission,
                                isCheckedByUi = false,
                                wouldBeFilteredIfChecked = !hasViewPermission
                            }
                        });
                    }

                    results[parentClass] = new
                    {
                        relation1NTabs,
                        relation1NTabCount = relation1NTabs.Count,
                        relationNNTabs,
                        relationNNTabCount = relationNNTabs.Count,
                        totalCustomTabs = relation1NTabs.Count + relationNNTabs.Count
                    };
                }

                return Ok(new
                {
                    success = true,
                    message = "Relation tabs as returned by BusinessFoundationDataService — NO per-metaclass permission filtering",
                    tabs = results,
                    issue = "GetRelation1NTabs() and GetRelationNNTabs() return ALL custom metaclass tabs regardless of AddPermissions() settings",
                    nextStep = "Call /show-what-should-happen to see how tabs SHOULD be filtered if the UI checked per-metaclass permissions"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 5: Demonstrate what SHOULD happen if the UI checked per-metaclass permissions.
        /// Compares filtered vs unfiltered results for the current user.
        /// Sample usage: https://localhost:5000/util-api/custom-meta-class-permission/show-what-should-happen
        /// </summary>
        [HttpGet("show-what-should-happen")]
        public IActionResult ShowWhatShouldHappen()
        {
            try
            {
                var currentPrincipal = PrincipalInfo.CurrentPrincipal;
                var isAdmin = User.IsInRole(Roles.Administrators) || User.IsInRole(CommerceRoles.CommerceAdmins);

                var orgMetaClass = DataContext.Current.MetaModel.MetaClasses["Organization"];
                if (orgMetaClass == null)
                    return BadRequest("Organization metaclass not found");

                var systemClasses = new List<string> { "Contact", "Organization", "Address", "CreditCard" };

                var allTabs = new List<object>();
                var filteredTabs = new List<object>();

                // Check 1:N tabs
                foreach (MetaField field in orgMetaClass.FindReferencesTo(true))
                {
                    if (field.MetaClass.IsBridge || field.MetaClass.IsCard)
                        continue;

                    if (systemClasses.Contains(field.MetaClass.Name) && field.AccessLevel == Mediachase.BusinessFoundation.Data.Meta.Management.AccessLevel.System)
                        continue;

                    var viewPermKey = $"businessfoundation:{field.MetaClass.Name.ToLower()}:view:permission";
                    var listPermKey = $"businessfoundation:{field.MetaClass.Name.ToLower()}:list:permission";
                    var viewPermType = new PermissionType(EPiCommerce, viewPermKey);
                    var listPermType = new PermissionType(EPiCommerce, listPermKey);
                    var hasView = _permissionService.IsPermitted(currentPrincipal, viewPermType);
                    var hasList = _permissionService.IsPermitted(currentPrincipal, listPermType);

                    var tab = new
                    {
                        type = "1:N",
                        refClassName = field.MetaClass.Name,
                        displayName = field.MetaClass.FriendlyName,
                        hasViewPermission = hasView,
                        hasListPermission = hasList,
                        wouldBeVisible = hasView || hasList
                    };

                    allTabs.Add(tab);
                    if (hasView || hasList)
                        filteredTabs.Add(tab);
                }

                // Check N:N tabs
                foreach (var bridgeClass in DataContext.Current.MetaModel.GetBridgesReferencedToClass(orgMetaClass))
                {
                    if (!bridgeClass.Attributes.ContainsKey(MetaClassAttribute.BridgeRef1Name)
                        || !bridgeClass.Attributes.ContainsKey(MetaClassAttribute.BridgeRef2Name))
                        continue;

                    var bridgeFields = bridgeClass.GetBridgeFields();
                    var refClassName = "Organization" == bridgeFields[0].ReferenceToMetaClassName
                        ? bridgeFields[1].ReferenceToMetaClassName
                        : bridgeFields[0].ReferenceToMetaClassName;

                    var viewPermKey = $"businessfoundation:{refClassName.ToLower()}:view:permission";
                    var listPermKey = $"businessfoundation:{refClassName.ToLower()}:list:permission";
                    var viewPermType = new PermissionType(EPiCommerce, viewPermKey);
                    var listPermType = new PermissionType(EPiCommerce, listPermKey);
                    var hasView = _permissionService.IsPermitted(currentPrincipal, viewPermType);
                    var hasList = _permissionService.IsPermitted(currentPrincipal, listPermType);

                    var tab = new
                    {
                        type = "N:N",
                        bridgeClassName = bridgeClass.Name,
                        refClassName,
                        displayName = bridgeClass.FriendlyName,
                        hasViewPermission = hasView,
                        hasListPermission = hasList,
                        wouldBeVisible = hasView || hasList
                    };

                    allTabs.Add(tab);
                    if (hasView || hasList)
                        filteredTabs.Add(tab);
                }

                return Ok(new
                {
                    success = true,
                    currentUser = User.Identity?.Name,
                    isAdmin,
                    organizationRelationTabs = new
                    {
                        currentBehavior = new
                        {
                            description = "ALL tabs shown regardless of per-metaclass permissions",
                            tabCount = allTabs.Count,
                            tabs = allTabs
                        },
                        expectedBehavior = new
                        {
                            description = "Only tabs where user has view/list permission should be shown",
                            tabCount = filteredTabs.Count,
                            tabs = filteredTabs
                        },
                        tabsHiddenIfFixed = allTabs.Count - filteredTabs.Count
                    },
                    conclusion = allTabs.Count == filteredTabs.Count
                        ? "Current user has all permissions — no difference (try with a non-admin user)"
                        : $"{allTabs.Count - filteredTabs.Count} tab(s) would be hidden if per-metaclass permissions were checked"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Step 6: Show the controller-level authorization gap.
        /// Compares how ContactsController/OrganizationsController use [Permissions] attributes vs BusinessFoundationDataController.
        /// Sample usage: https://localhost:5000/util-api/custom-meta-class-permission/show-controller-auth-gap
        /// </summary>
        [HttpGet("show-controller-auth-gap")]
        public IActionResult ShowControllerAuthGap()
        {
            try
            {
                return Ok(new
                {
                    success = true,
                    comparison = new
                    {
                        contactsController = new
                        {
                            description = "ContactsController uses [Permissions] attribute on each action",
                            example = "[Permissions(new string[] { CustomersPermissions.ContactListPermission })]",
                            permissionsChecked = new[]
                            {
                                CustomersPermissions.ContactListPermission,
                                CustomersPermissions.ContactViewPermission,
                                CustomersPermissions.ContactCreatePermission,
                                CustomersPermissions.ContactEditPermission,
                                CustomersPermissions.ContactDeletePermission
                            },
                            enforced = true
                        },
                        organizationsController = new
                        {
                            description = "OrganizationsController uses [Permissions] attribute on each action",
                            example = "[Permissions(new string[] { CustomersPermissions.OrgsListPermission })]",
                            permissionsChecked = new[]
                            {
                                CustomersPermissions.OrgsListPermission,
                                CustomersPermissions.OrgsViewPermission,
                                CustomersPermissions.OrgsCreatePermission,
                                CustomersPermissions.OrgsEditPermission,
                                CustomersPermissions.OrgsDeletePermission
                            },
                            enforced = true
                        },
                        businessFoundationDataController = new
                        {
                            description = "BusinessFoundationDataController uses only broad role-based [Authorize]",
                            example = "[Authorize(Roles = \"CommerceCustomers,BusinessFoundations,CommerceAdmins,Administrators\")]",
                            permissionsChecked = Array.Empty<string>(),
                            enforced = false,
                            gap = "No per-metaclass [Permissions] attribute — any user with the broad roles can access all BF data endpoints"
                        }
                    },
                    serverSideGap = "GetRelation1NTabs() and GetRelationNNTabs() return all tabs without permission filtering",
                    clientSideGap = "PermissionController.GetCurrentUserPermissions() does not include custom metaclass permissions in its response",
                    conclusion = "AddPermissions() registers permissions that are never checked at controller, service, or UI level for custom metaclasses"
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Cleanup: Remove the test custom metaclass and its data.
        /// Sample usage: https://localhost:5000/util-api/custom-meta-class-permission/cleanup
        /// </summary>
        [HttpGet("cleanup")]
        public IActionResult Cleanup()
        {
            try
            {
                var results = new List<object>();

                // Delete sample data
                try
                {
                    var manager = DataContext.Current.MetaModel;
                    if (manager.MetaClasses.Contains(TestClassName))
                    {
                        var entities = BusinessManager.List(TestClassName, new FilterElement[0]);
                        foreach (var entity in entities)
                        {
                            BusinessManager.Delete(entity);
                        }
                        results.Add(new { step = "DeleteData", success = true, count = entities.Length });
                    }
                    else
                    {
                        results.Add(new { step = "DeleteData", success = true, count = 0, message = "Class not found" });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new { step = "DeleteData", success = false, error = ex.Message });
                }

                // Delete metaclass
                try
                {
                    using (var scope = DataContext.Current.MetaModel.BeginEdit(MetaClassManagerEditScope.SystemOwner, Mediachase.BusinessFoundation.Data.Meta.Management.AccessLevel.System))
                    {
                        var manager = DataContext.Current.MetaModel;
                        if (manager.MetaClasses.Contains(TestClassName))
                        {
                            manager.MetaClasses.Remove(TestClassName);
                        }
                        scope.SaveChanges();
                        results.Add(new { step = "DeleteMetaClass", success = true });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new { step = "DeleteMetaClass", success = false, error = ex.Message });
                }

                return Ok(new
                {
                    success = true,
                    message = "Cleanup completed",
                    cleanupResults = results
                });
            }
            catch (Exception ex)
            {
                return BadRequest($"Exception: {ex.Message}\n{ex.InnerException?.Message}\n{ex.StackTrace}");
            }
        }
    }
}
