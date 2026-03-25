using EPiServer.Cms.UI.AspNetIdentity;
using EPiServer.ServiceLocation;
using EPiServer.Shell.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;


namespace Foundation.Custom
{
    [ApiController]
    [Route("user-api")]
    public class UserApiController : ControllerBase
    {

        public UserApiController()
        {
           
        }


        [HttpGet]
        [Route("create-user")]
        public async Task<ActionResult<string>> CreateUser([FromQuery] string username = null)
        {
            var uiSignInManager = ServiceLocator.Current.GetInstance<UISignInManager>();
            var uiUserProvider = ServiceLocator.Current.GetInstance<UIUserProvider>();
            var uiRoleProvider = ServiceLocator.Current.GetInstance<UIRoleProvider>();
            var uiUserManager = ServiceLocator.Current.GetInstance<UIUserManager>();

            username = username ?? "sysadmin123";
            var email = username + "@episerver.com";
            var password = "P@ssw0rd";
            var log = "";
            string[] roles = { "Administrators" };

            var userList = uiUserProvider.FindUsersByNameAsync(username, 0, 1);
            IUIUser user = null;
            await foreach (var u in userList)
            {
                user = u;
                break;
            }

            if (user == null)
            {

                var result = await uiUserProvider.CreateUserAsync(username, password, email, null, null, true);
                foreach (var error in result.Errors)
                {
                    log += error;
                }
                if (result.Errors.Count() == 0)
                {
                    string role = roles.First();
                    var isExist = await uiRoleProvider.RoleExistsAsync(role);
                    if (!isExist)
                    {
                        await uiRoleProvider.CreateRoleAsync(role);
                    }
                    await uiRoleProvider.AddUserToRolesAsync(username, roles);
                    log += "Created successfully user: " + username;
                }
            }
            else
            {
                log += ("Failed to create as the user " + username + " already exist");
                return Ok(log);
            }
            log += "User name: " + username;
            log += "Password: " + password;
            log += "Email: " + email;

            return Ok(log);
        }
    }
}
