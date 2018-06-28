//  <copyright file="CodeZeroUserManager.cs" project="CodeZero.IdentityCore" solution="CodeZero">
//      Copyright (c) 2018 CodeZero Framework.  All rights reserved.
//  </copyright>
//  <author>Nasr Aldin M.</author>
//  <email>nasr2ldin@gmail.com</email>
//  <website>https://nasraldin.com/codezero</website>
//  <github>https://nasraldin.github.io/CodeZero</github>
//  <date>01/01/2018 01:00 AM</date>
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeZero.Application.Features;
using CodeZero.Authorization.Roles;
using CodeZero.Configuration;
using CodeZero.Configuration.Startup;
using CodeZero.Domain.Repositories;
using CodeZero.Domain.Services;
using CodeZero.Domain.Uow;
using CodeZero.Json;
using CodeZero.Localization;
using CodeZero.MultiTenancy;
using CodeZero.Organizations;
using CodeZero.Reflection;
using CodeZero.Runtime.Caching;
using CodeZero.Runtime.Session;
using CodeZero.UI;
using CodeZero.Identity;
using CodeZero.Identity.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace CodeZero.Authorization.Users
{
    public class CodeZeroUserManager<TRole, TUser> : UserManager<TUser>, IDomainService
        where TRole : CodeZeroRole<TUser>, new()
        where TUser : CodeZeroUser<TUser>
    {
        protected IUserPermissionStore<TUser> UserPermissionStore
        {
            get
            {
                if (!(Store is IUserPermissionStore<TUser>))
                {
                    throw new CodeZeroException("Store is not IUserPermissionStore");
                }

                return Store as IUserPermissionStore<TUser>;
            }
        }

        public ILocalizationManager LocalizationManager { get; set; }

        public ICodeZeroSession CodeZeroSession { get; set; }

        public FeatureDependencyContext FeatureDependencyContext { get; set; }

        protected CodeZeroRoleManager<TRole, TUser> RoleManager { get; }

        protected CodeZeroUserStore<TRole, TUser> CodeZeroStore { get; }

        public IMultiTenancyConfig MultiTenancy { get; set; }

        private readonly IPermissionManager _permissionManager;
        private readonly IUnitOfWorkManager _unitOfWorkManager;
        private readonly ICacheManager _cacheManager;
        private readonly IRepository<OrganizationUnit, long> _organizationUnitRepository;
        private readonly IRepository<UserOrganizationUnit, long> _userOrganizationUnitRepository;
        private readonly IOrganizationUnitSettings _organizationUnitSettings;
        private readonly ISettingManager _settingManager;
        private readonly IOptions<IdentityOptions> _optionsAccessor;

        public CodeZeroUserManager(
            CodeZeroRoleManager<TRole, TUser> roleManager,
            CodeZeroUserStore<TRole, TUser> store,
            IOptions<IdentityOptions> optionsAccessor,
            IPasswordHasher<TUser> passwordHasher,
            IEnumerable<IUserValidator<TUser>> userValidators,
            IEnumerable<IPasswordValidator<TUser>> passwordValidators,
            ILookupNormalizer keyNormalizer,
            IdentityErrorDescriber errors,
            IServiceProvider services,
            ILogger<UserManager<TUser>> logger,
            IPermissionManager permissionManager,
            IUnitOfWorkManager unitOfWorkManager,
            ICacheManager cacheManager,
            IRepository<OrganizationUnit, long> organizationUnitRepository,
            IRepository<UserOrganizationUnit, long> userOrganizationUnitRepository,
            IOrganizationUnitSettings organizationUnitSettings,
            ISettingManager settingManager)
            : base(
                store,
                optionsAccessor,
                passwordHasher,
                userValidators,
                passwordValidators,
                keyNormalizer,
                errors,
                services,
                logger)
        {
            _permissionManager = permissionManager;
            _unitOfWorkManager = unitOfWorkManager;
            _cacheManager = cacheManager;
            _organizationUnitRepository = organizationUnitRepository;
            _userOrganizationUnitRepository = userOrganizationUnitRepository;
            _organizationUnitSettings = organizationUnitSettings;
            _settingManager = settingManager;
            _optionsAccessor = optionsAccessor;

            CodeZeroStore = store;
            RoleManager = roleManager;
        }

        public override async Task<IdentityResult> CreateAsync(TUser user)
        {
            var result = await CheckDuplicateUsernameOrEmailAddressAsync(user.Id, user.UserName, user.EmailAddress);
            if (!result.Succeeded)
            {
                return result;
            }

            var tenantId = GetCurrentTenantId();
            if (tenantId.HasValue && !user.TenantId.HasValue)
            {
                user.TenantId = tenantId.Value;
            }

            var isLockoutEnabled = user.IsLockoutEnabled;

            var identityResult = await base.CreateAsync(user);
            if (identityResult.Succeeded)
            {
                await SetLockoutEnabledAsync(user, isLockoutEnabled);
            }

            return identityResult;
        }

        /// <summary>
        /// Check whether a user is granted for a permission.
        /// </summary>
        /// <param name="userId">User id</param>
        /// <param name="permissionName">Permission name</param>
        public virtual async Task<bool> IsGrantedAsync(long userId, string permissionName)
        {
            return await IsGrantedAsync(
                userId,
                _permissionManager.GetPermission(permissionName)
                );
        }

        /// <summary>
        /// Check whether a user is granted for a permission.
        /// </summary>
        /// <param name="user">User</param>
        /// <param name="permission">Permission</param>
        public virtual Task<bool> IsGrantedAsync(TUser user, Permission permission)
        {
            return IsGrantedAsync(user.Id, permission);
        }

        /// <summary>
        /// Check whether a user is granted for a permission.
        /// </summary>
        /// <param name="userId">User id</param>
        /// <param name="permission">Permission</param>
        public virtual async Task<bool> IsGrantedAsync(long userId, Permission permission)
        {
            //Check for multi-tenancy side
            if (!permission.MultiTenancySides.HasFlag(GetCurrentMultiTenancySide()))
            {
                return false;
            }

            //Check for depended features
            if (permission.FeatureDependency != null && GetCurrentMultiTenancySide() == MultiTenancySides.Tenant)
            {
                FeatureDependencyContext.TenantId = GetCurrentTenantId();

                if (!await permission.FeatureDependency.IsSatisfiedAsync(FeatureDependencyContext))
                {
                    return false;
                }
            }

            //Get cached user permissions
            var cacheItem = await GetUserPermissionCacheItemAsync(userId);
            if (cacheItem == null)
            {
                return false;
            }

            //Check for user-specific value
            if (cacheItem.GrantedPermissions.Contains(permission.Name))
            {
                return true;
            }

            if (cacheItem.ProhibitedPermissions.Contains(permission.Name))
            {
                return false;
            }

            //Check for roles
            foreach (var roleId in cacheItem.RoleIds)
            {
                if (await RoleManager.IsGrantedAsync(roleId, permission))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets granted permissions for a user.
        /// </summary>
        /// <param name="user">Role</param>
        /// <returns>List of granted permissions</returns>
        public virtual async Task<IReadOnlyList<Permission>> GetGrantedPermissionsAsync(TUser user)
        {
            var permissionList = new List<Permission>();

            foreach (var permission in _permissionManager.GetAllPermissions())
            {
                if (await IsGrantedAsync(user.Id, permission))
                {
                    permissionList.Add(permission);
                }
            }

            return permissionList;
        }

        /// <summary>
        /// Sets all granted permissions of a user at once.
        /// Prohibits all other permissions.
        /// </summary>
        /// <param name="user">The user</param>
        /// <param name="permissions">Permissions</param>
        public virtual async Task SetGrantedPermissionsAsync(TUser user, IEnumerable<Permission> permissions)
        {
            var oldPermissions = await GetGrantedPermissionsAsync(user);
            var newPermissions = permissions.ToArray();

            foreach (var permission in oldPermissions.Where(p => !newPermissions.Contains(p)))
            {
                await ProhibitPermissionAsync(user, permission);
            }

            foreach (var permission in newPermissions.Where(p => !oldPermissions.Contains(p)))
            {
                await GrantPermissionAsync(user, permission);
            }
        }

        /// <summary>
        /// Prohibits all permissions for a user.
        /// </summary>
        /// <param name="user">User</param>
        public async Task ProhibitAllPermissionsAsync(TUser user)
        {
            foreach (var permission in _permissionManager.GetAllPermissions())
            {
                await ProhibitPermissionAsync(user, permission);
            }
        }

        /// <summary>
        /// Resets all permission settings for a user.
        /// It removes all permission settings for the user.
        /// User will have permissions according to his roles.
        /// This method does not prohibit all permissions.
        /// For that, use <see cref="ProhibitAllPermissionsAsync"/>.
        /// </summary>
        /// <param name="user">User</param>
        public async Task ResetAllPermissionsAsync(TUser user)
        {
            await UserPermissionStore.RemoveAllPermissionSettingsAsync(user);
        }

        /// <summary>
        /// Grants a permission for a user if not already granted.
        /// </summary>
        /// <param name="user">User</param>
        /// <param name="permission">Permission</param>
        public virtual async Task GrantPermissionAsync(TUser user, Permission permission)
        {
            await UserPermissionStore.RemovePermissionAsync(user, new PermissionGrantInfo(permission.Name, false));

            if (await IsGrantedAsync(user.Id, permission))
            {
                return;
            }

            await UserPermissionStore.AddPermissionAsync(user, new PermissionGrantInfo(permission.Name, true));
        }

        /// <summary>
        /// Prohibits a permission for a user if it's granted.
        /// </summary>
        /// <param name="user">User</param>
        /// <param name="permission">Permission</param>
        public virtual async Task ProhibitPermissionAsync(TUser user, Permission permission)
        {
            await UserPermissionStore.RemovePermissionAsync(user, new PermissionGrantInfo(permission.Name, true));

            if (!await IsGrantedAsync(user.Id, permission))
            {
                return;
            }

            await UserPermissionStore.AddPermissionAsync(user, new PermissionGrantInfo(permission.Name, false));
        }

        public virtual Task<TUser> FindByNameOrEmailAsync(string userNameOrEmailAddress)
        {
            return CodeZeroStore.FindByNameOrEmailAsync(userNameOrEmailAddress);
        }

        public virtual Task<List<TUser>> FindAllAsync(UserLoginInfo login)
        {
            return CodeZeroStore.FindAllAsync(login);
        }

        public virtual Task<TUser> FindAsync(int? tenantId, UserLoginInfo login)
        {
            return CodeZeroStore.FindAsync(tenantId, login);
        }

        public virtual Task<TUser> FindByNameOrEmailAsync(int? tenantId, string userNameOrEmailAddress)
        {
            return CodeZeroStore.FindByNameOrEmailAsync(tenantId, userNameOrEmailAddress);
        }

        /// <summary>
        /// Gets a user by given id.
        /// Throws exception if no user found with given id.
        /// </summary>
        /// <param name="userId">User id</param>
        /// <returns>User</returns>
        /// <exception cref="CodeZeroException">Throws exception if no user found with given id</exception>
        public virtual async Task<TUser> GetUserByIdAsync(long userId)
        {
            var user = await FindByIdAsync(userId.ToString());
            if (user == null)
            {
                throw new CodeZeroException("There is no user with id: " + userId);
            }

            return user;
        }

        public override async Task<IdentityResult> UpdateAsync(TUser user)
        {
            var result = await CheckDuplicateUsernameOrEmailAddressAsync(user.Id, user.UserName, user.EmailAddress);
            if (!result.Succeeded)
            {
                return result;
            }

            //Admin user's username can not be changed!
            if (user.UserName != CodeZeroUserBase.AdminUserName)
            {
                if ((await GetOldUserNameAsync(user.Id)) == CodeZeroUserBase.AdminUserName)
                {
                    throw new UserFriendlyException(string.Format(L("CanNotRenameAdminUser"), CodeZeroUserBase.AdminUserName));
                }
            }

            return await base.UpdateAsync(user);
        }

        public override async Task<IdentityResult> DeleteAsync(TUser user)
        {
            if (user.UserName == CodeZeroUserBase.AdminUserName)
            {
                throw new UserFriendlyException(string.Format(L("CanNotDeleteAdminUser"), CodeZeroUserBase.AdminUserName));
            }

            return await base.DeleteAsync(user);
        }

        public virtual async Task<IdentityResult> ChangePasswordAsync(TUser user, string newPassword)
        {
            var errors = new List<IdentityError>();

            foreach (var validator in PasswordValidators)
            {
                var validationResult = await validator.ValidateAsync(this, user, newPassword);
                if (!validationResult.Succeeded)
                {
                    errors.AddRange(validationResult.Errors);
                }
            }

            if (errors.Any())
            {
                return IdentityResult.Failed(errors.ToArray());
            }

            await CodeZeroStore.SetPasswordHashAsync(user, PasswordHasher.HashPassword(user, newPassword));
            return IdentityResult.Success;
        }

        public virtual async Task<IdentityResult> CheckDuplicateUsernameOrEmailAddressAsync(long? expectedUserId, string userName, string emailAddress)
        {
            var user = (await FindByNameAsync(userName));
            if (user != null && user.Id != expectedUserId)
            {
                throw new UserFriendlyException(string.Format(L("Identity.DuplicateUserName"), userName));
            }

            user = (await FindByEmailAsync(emailAddress));
            if (user != null && user.Id != expectedUserId)
            {
                throw new UserFriendlyException(string.Format(L("Identity.DuplicateEmail"), emailAddress));
            }

            return IdentityResult.Success;
        }

        public virtual async Task<IdentityResult> SetRoles(TUser user, string[] roleNames)
        {
            await CodeZeroStore.UserRepository.EnsureCollectionLoadedAsync(user, u => u.Roles);

            //Remove from removed roles
            foreach (var userRole in user.Roles.ToList())
            {
                var role = await RoleManager.FindByIdAsync(userRole.RoleId.ToString());
                if (roleNames.All(roleName => role.Name != roleName))
                {
                    var result = await RemoveFromRoleAsync(user, role.Name);
                    if (!result.Succeeded)
                    {
                        return result;
                    }
                }
            }

            //Add to added roles
            foreach (var roleName in roleNames)
            {
                var role = await RoleManager.GetRoleByNameAsync(roleName);
                if (user.Roles.All(ur => ur.RoleId != role.Id))
                {
                    var result = await AddToRoleAsync(user, roleName);
                    if (!result.Succeeded)
                    {
                        return result;
                    }
                }
            }

            return IdentityResult.Success;
        }

        public virtual async Task<bool> IsInOrganizationUnitAsync(long userId, long ouId)
        {
            return await IsInOrganizationUnitAsync(
                await GetUserByIdAsync(userId),
                await _organizationUnitRepository.GetAsync(ouId)
                );
        }

        public virtual async Task<bool> IsInOrganizationUnitAsync(TUser user, OrganizationUnit ou)
        {
            return await _userOrganizationUnitRepository.CountAsync(uou =>
                uou.UserId == user.Id && uou.OrganizationUnitId == ou.Id
                ) > 0;
        }

        public virtual async Task AddToOrganizationUnitAsync(long userId, long ouId)
        {
            await AddToOrganizationUnitAsync(
                await GetUserByIdAsync(userId),
                await _organizationUnitRepository.GetAsync(ouId)
                );
        }

        public virtual async Task AddToOrganizationUnitAsync(TUser user, OrganizationUnit ou)
        {
            var currentOus = await GetOrganizationUnitsAsync(user);

            if (currentOus.Any(cou => cou.Id == ou.Id))
            {
                return;
            }

            await CheckMaxUserOrganizationUnitMembershipCountAsync(user.TenantId, currentOus.Count + 1);

            await _userOrganizationUnitRepository.InsertAsync(new UserOrganizationUnit(user.TenantId, user.Id, ou.Id));
        }

        public virtual async Task RemoveFromOrganizationUnitAsync(long userId, long ouId)
        {
            await RemoveFromOrganizationUnitAsync(
                await GetUserByIdAsync(userId),
                await _organizationUnitRepository.GetAsync(ouId)
                );
        }

        public virtual async Task RemoveFromOrganizationUnitAsync(TUser user, OrganizationUnit ou)
        {
            await _userOrganizationUnitRepository.DeleteAsync(uou => uou.UserId == user.Id && uou.OrganizationUnitId == ou.Id);
        }

        public virtual async Task SetOrganizationUnitsAsync(long userId, params long[] organizationUnitIds)
        {
            await SetOrganizationUnitsAsync(
                await GetUserByIdAsync(userId),
                organizationUnitIds
                );
        }

        private async Task CheckMaxUserOrganizationUnitMembershipCountAsync(int? tenantId, int requestedCount)
        {
            var maxCount = await _organizationUnitSettings.GetMaxUserMembershipCountAsync(tenantId);
            if (requestedCount > maxCount)
            {
                throw new CodeZeroException($"Can not set more than {maxCount} organization unit for a user!");
            }
        }

        public virtual async Task SetOrganizationUnitsAsync(TUser user, params long[] organizationUnitIds)
        {
            if (organizationUnitIds == null)
            {
                organizationUnitIds = new long[0];
            }

            await CheckMaxUserOrganizationUnitMembershipCountAsync(user.TenantId, organizationUnitIds.Length);

            var currentOus = await GetOrganizationUnitsAsync(user);

            //Remove from removed OUs
            foreach (var currentOu in currentOus)
            {
                if (!organizationUnitIds.Contains(currentOu.Id))
                {
                    await RemoveFromOrganizationUnitAsync(user, currentOu);
                }
            }

            //Add to added OUs
            foreach (var organizationUnitId in organizationUnitIds)
            {
                if (currentOus.All(ou => ou.Id != organizationUnitId))
                {
                    await AddToOrganizationUnitAsync(
                        user,
                        await _organizationUnitRepository.GetAsync(organizationUnitId)
                        );
                }
            }
        }

        [UnitOfWork]
        public virtual Task<List<OrganizationUnit>> GetOrganizationUnitsAsync(TUser user)
        {
            var query = from uou in _userOrganizationUnitRepository.GetAll()
                        join ou in _organizationUnitRepository.GetAll() on uou.OrganizationUnitId equals ou.Id
                        where uou.UserId == user.Id
                        select ou;

            return Task.FromResult(query.ToList());
        }

        [UnitOfWork]
        public virtual Task<List<TUser>> GetUsersInOrganizationUnit(OrganizationUnit organizationUnit, bool includeChildren = false)
        {
            if (!includeChildren)
            {
                var query = from uou in _userOrganizationUnitRepository.GetAll()
                            join user in Users on uou.UserId equals user.Id
                            where uou.OrganizationUnitId == organizationUnit.Id
                            select user;

                return Task.FromResult(query.ToList());
            }
            else
            {
                var query = from uou in _userOrganizationUnitRepository.GetAll()
                            join user in Users on uou.UserId equals user.Id
                            join ou in _organizationUnitRepository.GetAll() on uou.OrganizationUnitId equals ou.Id
                            where ou.Code.StartsWith(organizationUnit.Code)
                            select user;

                return Task.FromResult(query.ToList());
            }
        }

        public virtual async Task InitializeOptionsAsync(int? tenantId)
        {
            Options = JsonConvert.DeserializeObject<IdentityOptions>(_optionsAccessor.Value.ToJsonString());

            //Lockout
            Options.Lockout.AllowedForNewUsers = await IsTrueAsync(CodeZeroSettingNames.UserManagement.UserLockOut.IsEnabled, tenantId);
            Options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromSeconds(await GetSettingValueAsync<int>(CodeZeroSettingNames.UserManagement.UserLockOut.DefaultAccountLockoutSeconds, tenantId));
            Options.Lockout.MaxFailedAccessAttempts = await GetSettingValueAsync<int>(CodeZeroSettingNames.UserManagement.UserLockOut.MaxFailedAccessAttemptsBeforeLockout, tenantId);

            //Password complexity
            Options.Password.RequireDigit = await GetSettingValueAsync<bool>(CodeZeroSettingNames.UserManagement.PasswordComplexity.RequireDigit, tenantId);
            Options.Password.RequireLowercase = await GetSettingValueAsync<bool>(CodeZeroSettingNames.UserManagement.PasswordComplexity.RequireLowercase, tenantId);
            Options.Password.RequireNonAlphanumeric = await GetSettingValueAsync<bool>(CodeZeroSettingNames.UserManagement.PasswordComplexity.RequireNonAlphanumeric, tenantId);
            Options.Password.RequireUppercase = await GetSettingValueAsync<bool>(CodeZeroSettingNames.UserManagement.PasswordComplexity.RequireUppercase, tenantId);
            Options.Password.RequiredLength = await GetSettingValueAsync<int>(CodeZeroSettingNames.UserManagement.PasswordComplexity.RequiredLength, tenantId);
        }

        protected virtual Task<string> GetOldUserNameAsync(long userId)
        {
            return CodeZeroStore.GetUserNameFromDatabaseAsync(userId);
        }

        private async Task<UserPermissionCacheItem> GetUserPermissionCacheItemAsync(long userId)
        {
            var cacheKey = userId + "@" + (GetCurrentTenantId() ?? 0);
            return await _cacheManager.GetUserPermissionCache().GetAsync(cacheKey, async () =>
            {
                var user = await FindByIdAsync(userId.ToString());
                if (user == null)
                {
                    return null;
                }

                var newCacheItem = new UserPermissionCacheItem(userId);

                foreach (var roleName in await GetRolesAsync(user))
                {
                    newCacheItem.RoleIds.Add((await RoleManager.GetRoleByNameAsync(roleName)).Id);
                }

                foreach (var permissionInfo in await UserPermissionStore.GetPermissionsAsync(userId))
                {
                    if (permissionInfo.IsGranted)
                    {
                        newCacheItem.GrantedPermissions.Add(permissionInfo.Name);
                    }
                    else
                    {
                        newCacheItem.ProhibitedPermissions.Add(permissionInfo.Name);
                    }
                }

                return newCacheItem;
            });
        }

        public override async Task<IList<string>> GetValidTwoFactorProvidersAsync(TUser user)
        {
            var providers = new List<string>();

            foreach (var provider in await base.GetValidTwoFactorProvidersAsync(user))
            {
                if (provider == "Email" &&
                    !await IsTrueAsync(CodeZeroSettingNames.UserManagement.TwoFactorLogin.IsEmailProviderEnabled, user.TenantId))
                {
                    continue;
                }

                if (provider == "Phone" &&
                    !await IsTrueAsync(CodeZeroSettingNames.UserManagement.TwoFactorLogin.IsSmsProviderEnabled, user.TenantId))
                {
                    continue;
                }

                providers.Add(provider);
            }

            return providers;
        }

        private bool IsTrue(string settingName, int? tenantId)
        {
            return GetSettingValue<bool>(settingName, tenantId);
        }

        private Task<bool> IsTrueAsync(string settingName, int? tenantId)
        {
            return GetSettingValueAsync<bool>(settingName, tenantId);
        }

        private T GetSettingValue<T>(string settingName, int? tenantId) where T : struct
        {
            return tenantId == null
                ? _settingManager.GetSettingValueForApplication<T>(settingName)
                : _settingManager.GetSettingValueForTenant<T>(settingName, tenantId.Value);
        }

        private Task<T> GetSettingValueAsync<T>(string settingName, int? tenantId) where T : struct
        {
            return tenantId == null
                ? _settingManager.GetSettingValueForApplicationAsync<T>(settingName)
                : _settingManager.GetSettingValueForTenantAsync<T>(settingName, tenantId.Value);
        }

        private string L(string name)
        {
            return LocalizationManager.GetString(CodeZeroConsts.LocalizationSourceName, name);
        }

        private int? GetCurrentTenantId()
        {
            if (_unitOfWorkManager.Current != null)
            {
                return _unitOfWorkManager.Current.GetTenantId();
            }

            return CodeZeroSession.TenantId;
        }

        private MultiTenancySides GetCurrentMultiTenancySide()
        {
            if (_unitOfWorkManager.Current != null)
            {
                return MultiTenancy.IsEnabled && !_unitOfWorkManager.Current.GetTenantId().HasValue
                    ? MultiTenancySides.Host
                    : MultiTenancySides.Tenant;
            }

            return CodeZeroSession.MultiTenancySide;
        }
    }
}