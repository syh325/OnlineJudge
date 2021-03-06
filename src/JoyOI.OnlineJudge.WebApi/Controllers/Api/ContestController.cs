﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using JoyOI.OnlineJudge.ContestExecutor;
using JoyOI.OnlineJudge.Models;
using JoyOI.OnlineJudge.WebApi.Models;

namespace JoyOI.OnlineJudge.WebApi.Controllers.Api
{
    [Route("api/[controller]")]
    public class ContestController : BaseController
    {
        #region Contest
        [HttpGet("all")]
        public Task<IActionResult> Get(
            string title, 
            ContestType? type, 
            AttendPermission? attendPermission, 
            bool? highlight,
            DateTime? begin, 
            DateTime? end,
            int? page,
            int? pageSize,
            CancellationToken token)
        {
            IQueryable<Contest> ret = DB.Contests;
            if (!string.IsNullOrWhiteSpace(title))
            {
                ret = ret.Where(x => x.Title.Contains(title) || title.Contains(x.Title));
            }
            if (type.HasValue)
            {
                ret = ret.Where(x => x.Type == type.Value);
            }
            if (attendPermission.HasValue)
            {
                ret = ret.Where(x => x.AttendPermission == attendPermission.Value);
            }
            if (begin.HasValue)
            {
                ret = ret.Where(x => x.Begin >= begin.Value);
            }
            if (end.HasValue)
            {
                ret = ret.Where(x => x.Begin.Add(x.Duration) <= end.Value);
            }
            if (highlight.HasValue && highlight.Value)
            {
                ret = ret.Where(x => x.IsHighlighted);
            }
            if (IsGroupRequest())
            {
                ret = ret.Where(x => x.AttendPermission == AttendPermission.Team
                && x.PasswordOrTeamId == CurrentGroup.Id
                || DB.GroupContestReferences.Where(y => y.GroupId == CurrentGroup.Id).Select(y => y.ContestId).Contains(x.Id));
            }
            else
            {
                ret = ret.Where(x => x.AttendPermission != AttendPermission.Team);
            }

            if (!page.HasValue)
            {
                page = 1;
            }

            if (!pageSize.HasValue)
            {
                pageSize = 5;
            }

            return Paged(ret
                .OrderByDescending(x => x.Begin.Add(x.Duration))
                .ThenByDescending(x => x.Begin), 
                page.Value,
                pageSize.Value, 
                token);
        }

        [HttpGet("{id:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}")]
        public async Task<IActionResult> Get(string id, CancellationToken token)
        {
            var ret = await DB.Contests
                .SingleOrDefaultAsync(x => x.Id == id, token);
            if (ret == null)
            {
                return Result<Contest>(404, "Not found");
            }
            else
            {
                FilterEntity(ret);
                return Result(ret);
            }
        }

        [HttpGet("{id:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/session")]
        public async Task<IActionResult> GetSession(string id, [FromServices] ContestExecutorFactory cef, CancellationToken token)
        {
            var contest = await DB.Contests.SingleOrDefaultAsync(x => x.Id == id, token);
            if (contest == null)
            {
                return Result(404, "The contest is not found");
            }

            var ce = cef.Create(contest.Id);

            JoyOI.OnlineJudge.Models.Attendee attendee = null;
            if (User.IsSignedIn())
            {
                attendee = await DB.Attendees.SingleOrDefaultAsync(x => x.ContestId == id && x.UserId == User.Current.Id, token);
            }
            if (attendee == null)
            {
                return Result(new
                {
                    isRegistered = false,
                    begin = contest.Begin,
                    end = contest.Begin.Add(contest.Duration),
                    isBegan = DateTime.UtcNow > contest.Begin,
                    isEnded = DateTime.UtcNow > contest.Begin.Add(contest.Duration),
                    isStandingsAvailable = ce.IsAvailableToGetStandings() || ce.HasPermissionToContest(),
                    allowLock = false
                });
            }
            else
            {
                return Result(new
                {
                    isRegistered = true,
                    isVirtual = attendee.IsVirtual,
                    begin = attendee.IsVirtual ? attendee.RegisterTime : contest.Begin,
                    end = attendee.IsVirtual ? attendee.RegisterTime.Add(contest.Duration) : contest.Begin.Add(contest.Duration),
                    isBegan = DateTime.UtcNow > (attendee.IsVirtual ? attendee.RegisterTime : contest.Begin),
                    isEnded = DateTime.UtcNow > (attendee.IsVirtual ? attendee.RegisterTime.Add(contest.Duration) : contest.Begin.Add(contest.Duration)),
                    isStandingsAvailable = ce.IsAvailableToGetStandings(User.Current.UserName) || ce.HasPermissionToContest(),
                    allowLock = ce.AllowLockProblem
                });
            }
        }

        [HttpPost("{id:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}")]
        [HttpPatch("{id:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}")]
        public async Task<IActionResult> Patch(string id, [FromServices] ContestExecutorFactory cef, CancellationToken token)
        {
            var ce = cef.Create(id);
            if (!ce.HasPermissionToContest())
            {
                return Result(401, "No Permission");
            }
            else
            {
                var contest = await DB.Contests
                    .SingleOrDefaultAsync(x => x.Id == id, token);

                if (contest == null)
                {
                    return Result(404, "Contest not found");
                }
               
                var fields = PatchEntity(contest, RequestBody);
                if (fields.Any(x => x == nameof(contest.IsHighlighted)) && !IsMasterOrHigher)
                {
                    return Result(403, "You don't have the permission to set the contest highlight.");
                }

                if (fields.Contains(nameof(Contest.Begin)))
                {
                    if (contest.Begin < DateTime.UtcNow)
                    {
                        return Result(400, "Invalid begin time");
                    }
                }

                if (fields.Contains(nameof(Contest.Domain)) && !string.IsNullOrWhiteSpace(contest.Domain) && !await DB.Contests.AnyAsync(x => x.Domain == contest.Domain, token))
                {
                    return Result(400, "The domain is already existed.");
                }

                await DB.SaveChangesAsync(token);

                return Result(200, "Patch succeeded");
            }
        }

        [HttpPut("{id:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}")]
        public async Task<IActionResult> Put(string id, CancellationToken token)
        {
            if (await DB.Contests.AnyAsync(x => x.Id == id, token))
            {
                return Result(400, "The contest id is already exists.");
            }
            else
            {
                var contest = PutEntity<Contest>(RequestBody).Entity;
                contest.Id = id;

                if (IsGroupRequest() && contest.AttendPermission == AttendPermission.Team)
                {
                    contest.PasswordOrTeamId = CurrentGroup.Id;
                }

                if (contest.Begin < DateTime.UtcNow)
                {
                    return Result(400, "The begin time is invalid.");
                }
                if (!string.IsNullOrWhiteSpace(contest.Domain) && await DB.Contests.AnyAsync(x => x.Domain == contest.Domain, token))
                {
                    return Result(400, "The domain is already existed.");
                }
                if (!IsMasterOrHigher && contest.Type == ContestType.OI && contest.Duration > new TimeSpan(3, 0, 0, 0, 0))
                {
                    return Result(400, "The duration of OI contest must lower than 3 days.");
                }
                if (!IsMasterOrHigher && contest.Duration > new TimeSpan(7, 0, 0, 0, 0))
                {
                    return Result(400, "The duration must lower than 7 days.");
                }
                if (IsGroupRequest())
                {
                    if (!await HasPermissionToGroupAsync(token))
                    {
                        return Result(400, "You don't have the permission to create a contest in this group.");
                    }
                    else if (contest.AttendPermission == AttendPermission.Team)
                    {
                        contest.PasswordOrTeamId = CurrentGroup.Id;
                    }
                }

                DB.Contests.Add(contest);
                DB.UserClaims.Add(new IdentityUserClaim<Guid> { ClaimType = Constants.ContestEditPermission, ClaimValue = id, UserId = User.Current.Id });

                if (IsGroupRequest() && contest.AttendPermission == AttendPermission.Everyone)
                {
                    DB.GroupContestReferences.Add(new GroupContestReference
                    {
                        ContestId = contest.Id,
                        GroupId = CurrentGroup.Id
                    });
                }

                await DB.SaveChangesAsync(token);
                return Result(200, "Put succeeded");
            }
        }

        [HttpDelete("{id:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}")]
        public async Task<IActionResult> Delete(string id, [FromServices] ContestExecutorFactory cef, CancellationToken token)
        {
            var ce = cef.Create(id);
            if (!ce.HasPermissionToContest())
            {
                return Result(401, "No permission");
            }
            else
            {
                var contest = await DB.Contests.SingleOrDefaultAsync(x => x.Id == id, token);
                if (contest == null)
                {
                    return Result(404, "Contest not found");
                }
                else if (contest.Begin <= DateTime.UtcNow)
                {
                    return Result(400, "Cannot remove a started contest.");
                }
                else
                {
                    await DB.UserClaims
                        .Where(x => x.ClaimType == Constants.ContestEditPermission)
                        .Where(x => x.ClaimValue == id)
                        .DeleteAsync(token);

                    return Result(200, "Delete succeeded");
                }
            }
        }
        #endregion

        #region Contest Problem
        [HttpGet("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/problem/all")]
        public async Task<IActionResult> GetContestProblems(string contestId, [FromServices] ContestExecutorFactory cef, CancellationToken token)
        {
            var contest = await DB.Contests
                .Include(x => x.Problems)
                .SingleOrDefaultAsync(x => x.Id == contestId, token);

            if (contest == null)
            {
                return Result<IEnumerable<ContestProblemViewModel>>(404, "Contest not found");
            }

            var ce = cef.Create(contestId);
            if (contest.Begin > DateTime.UtcNow && !ce.HasPermissionToContest())
            {
                return Result<IEnumerable<ContestProblemViewModel>>(400, "The contest has not started");
            }
            else if (contest.End >= DateTime.UtcNow && !await IsRegisteredToContest(contestId, token) && !ce.HasPermissionToContest())
            {
                return Result<IEnumerable<ContestProblemViewModel>>(new ContestProblemViewModel[] { });
            }
            else
            {
                var userId = User.Current?.Id;
                var locked = await DB.ContestProblemLastStatuses
                    .Where(x => x.ContestId == contestId)
                    .Where(x => x.IsLocked)
                    .Where(x => x.UserId == userId)
                    .Select(x => x.ProblemId)
                    .ToListAsync(token);
                var ret = await DB.ContestProblems
                    .Include(x => x.Problem)
                    .Where(x => x.ContestId == contestId)
                    .OrderBy(x => x.Number)
                    .ThenBy(x => x.Point)
                    .Select(x => new ContestProblemViewModel
                    {
                        problemId = x.ProblemId,
                        number = x.Number,
                        point = x.Point,
                        isVisible = x.Problem.IsVisible,
                        isLocked = locked.Contains(x.ProblemId)
                    })
                    .ToListAsync(token);

                if (User.IsSignedIn())
                {
                    foreach (var x in ret)
                    {
                        x.status = ce.GenerateProblemStatusText(x.problemId, User.Current.UserName);
                    }
                }

                return Result<IEnumerable<ContestProblemViewModel>>(ret);
            }
        }

        [HttpPut("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/problem/{problemId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}")]
        public async Task<IActionResult> PutContestProblem(string contestId, string problemId, [FromServices]ContestExecutorFactory cef, CancellationToken token)
        {
            var contest = await DB.Contests
                .SingleOrDefaultAsync(x => x.Id == contestId, token);
            var problem = await DB.Problems
                .SingleOrDefaultAsync(x => x.Id == problemId, token);
            var problemCount = await DB.ContestProblems.CountAsync(x => x.ContestId == contestId);
            if (contest == null)
            {
                return Result(404, "Contest not found");
            }
            else if (problem == null)
            {
                return Result(404, "Problem not found");
            }

            var ce = cef.Create(contestId);
            if (problemCount >= 26)
            {
                return Result(400, "The contest problem count cannot be greater than 26");
            }
            else if (!ce.HasPermissionToContest())
            {
                return Result(401, "No permission to this contest");
            }
            else if (!problem.IsVisible && !await HasPermissionToProblemAsync(contestId, token))
            {
                return Result(401, "No permission to this problem");
            }
            else if (await DB.ContestProblems.AnyAsync(x => x.ContestId == contestId && x.ProblemId == problemId, token))
            {
                return Result(400, "The problem has been already added into this contest");
            }
            else
            {
                var contestProblem = PutEntity<ContestProblem>(RequestBody);
                contestProblem.Entity.ContestId = contestId;
                contestProblem.Entity.ProblemId = problemId;

                if (contestProblem.Fields.Contains("Number"))
                {
                    if (await DB.ContestProblems.AnyAsync(x => x.ContestId == contestId && x.Number == contestProblem.Entity.Number, token))
                    {
                        return Result(400, "The problem number is already existed.");
                    }
                }
                else
                {
                    contestProblem.Entity.Number = ProblemNumberString[problemCount].ToString();
                }

                DB.ContestProblems.Add(contestProblem.Entity);
                await DB.SaveChangesAsync(token);
                return Result(200, "Put succeeded");
            }
        }

        [HttpPost("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/problem/{problemId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}")]
        [HttpPatch("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/problem/{problemId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}")]
        public async Task<IActionResult> PatchContestProblem(string contestId, string problemId, [FromServices] ContestExecutorFactory cef, [FromBody] string value, CancellationToken token)
        {
            var contestProblem = await DB.ContestProblems
                .SingleOrDefaultAsync(x => x.ContestId == contestId && x.ProblemId == problemId, token);
            if (contestProblem == null)
            {
                return Result(404, "Contest problem not found");
            }

            var ce = cef.Create(contestId);
            if (!ce.HasPermissionToContest())
            {
                return Result(401, "No permission to this contest");
            }
            else
            {
                var fields = PatchEntity(contestProblem, value);
                if (fields.Contains(nameof(ContestProblem.Number)))
                {
                    if (await DB.ContestProblems.AnyAsync(x => x.ContestId == contestId && x.Number == contestProblem.Number))
                    {
                        return Result(400, "The problem number is already existed.");
                    }
                }
                await DB.SaveChangesAsync(token);
                return Result(200, "Patch succeeded");
            }
        }

        [HttpDelete("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/problem/{problemId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}")]
        public async Task<IActionResult> DeleteContestProblem(string contestId, string problemId, [FromServices] ContestExecutorFactory cef, [FromBody] string value, CancellationToken token)
        {
            var contestProblem = await DB.ContestProblems
                .SingleOrDefaultAsync(x => x.ContestId == contestId && x.ProblemId == problemId, token);
            if (contestProblem == null)
            {
                return Result(404, "Contest problem not found");
            }

            var ce = cef.Create(contestId);
            if (!ce.HasPermissionToContest())
            {
                return Result(401, "No permission to this contest");
            }
            else
            {
                await DB.JudgeStatuses
                    .Where(x => x.ContestId == contestId)
                    .Where(x => x.ProblemId == problemId)
                    .DeleteAsync(token);

                await DB.ContestProblems
                    .Where(x => x.ProblemId == problemId)
                    .Where(x => x.ContestId == contestId)
                    .DeleteAsync(token);

                return Result(200, "Delete succeeded");
            }
        }
        #endregion

        #region Register
        [HttpPut("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/register")]
        public async Task<IActionResult> PutRegister(string contestId, CancellationToken token)
        {
            var contest = await DB.Contests.SingleOrDefaultAsync(x => x.Id == contestId, token);
            if (contest == null)
            {
                return Result(404, "The contest is not found");
            }

            var request = JsonConvert.DeserializeObject<ContestRegisterRequest>(RequestBody);

            if (contest.DisableVirtual && request.isVirtual)
            {
                return Result(400, "This contest does not accept a virtual competitor");
            }

            if (contest.AttendPermission == AttendPermission.Password && request.password != contest.PasswordOrTeamId)
            {
                return Result(400, "This password is incorrect");
            }

            if (contest.AttendPermission == AttendPermission.Team && !IsMasterOrHigher && !(await DB.GroupMembers.AnyAsync(x => x.UserId == User.Current.Id && x.GroupId == contest.PasswordOrTeamId)))
            {
                return Result(400, $"You are not a member of team '{ contest.PasswordOrTeamId }'");
            }

            if (contest.End <= DateTime.UtcNow && !request.isVirtual)
            {
                return Result(400, "The contest is end");
            }

            if (contest.Status == ContestStatus.Pending && request.isVirtual)
            {
                return Result(400, "You can only register as virtual after the contest began.");
            }

            var register = await DB.Attendees
                .Where(x => x.ContestId == contestId)
                .Where(x => x.UserId == User.Current.Id)
                .SingleOrDefaultAsync(token);

            if (register != null)
            {
                return Result(400, "You have already registered this contest.");
            }

            register = new OnlineJudge.Models.Attendee()
            {
                ContestId = contestId,
                IsVirtual = request.isVirtual,
                RegisterTime = DateTime.UtcNow,
                UserId = User.Current.Id
            };
            DB.Attendees.Add(register);
            await DB.SaveChangesAsync(token);

            DB.Contests
                .Where(x => x.Id == contestId)
                .SetField(x => x.CachedAttendeeCount).Plus(1)
                .SetField(x => x.CachedFormalAttendeeCount).Plus(register.IsVirtual ? 0 : 1)
                .Update();

            return Result(200, "Register succeeded");
        }
        #endregion

        #region Claims
        [HttpGet("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/claim/all")]
        public async Task<IActionResult> GetClaims(string contestId, CancellationToken token)
        {
            var ret = await DB.UserClaims
                .Where(x => x.ClaimType == Constants.ContestEditPermission)
                .Where(x => x.ClaimValue == contestId)
                .ToListAsync(token);
            return Result(ret);
        }

        [HttpPut("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/claim/{username:regex(^[[\u3040-\u309F\u30A0-\u30FF\u4e00-\u9fa5A-Za-z0-9_-]]{{4,128}}$)}")]
        public async Task<IActionResult> PutClaims(string contestId, string username, [FromServices]ContestExecutorFactory cef, CancellationToken token)
        {
            var ce = cef.Create(contestId);
            if (!ce.HasPermissionToContest())
            {
                return Result(401, "No permission");
            }
            var user = await User.Manager.FindByNameAsync(username);
            if (await DB.UserClaims.AnyAsync(x => x.ClaimValue == contestId && x.ClaimType == Constants.ContestEditPermission && x.UserId == user.Id, token))
            {
                return Result(400, "Already exists");
            }
            else
            {
                if (user == null)
                {
                    return Result(404, "User is not found");
                }
                else if (!(IsGroupRequest() && await HasPermissionToGroupAsync(token) && await DB.GroupMembers.AnyAsync(x => x.GroupId == CurrentGroup.Id && x.UserId == user.Id && x.Status == GroupMemberStatus.Approved)) && !IsMasterOrHigher)
                {
                    return Result(401, user.UserName + " is not a member of your group.");
                }
                else if (await DB.UserClaims.Where(x => x.ClaimType == Constants.ContestEditPermission && x.ClaimValue == contestId).CountAsync(token) >= 5)
                {
                    return Result(400, "Max 5 referees in a contest.");
                }
                DB.UserClaims.Add(new IdentityUserClaim<Guid>
                {
                    ClaimType = Constants.ContestEditPermission,
                    UserId = user.Id,
                    ClaimValue = contestId
                });
                await DB.SaveChangesAsync(token);
                return Result(200, "Succeeded");
            }
        }

        [HttpDelete("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/claim/{username:regex(^[[\u3040-\u309F\u30A0-\u30FF\u4e00-\u9fa5A-Za-z0-9_-]]{{4,128}}$)}")]
        public async Task<IActionResult> DeleteClaim(string username, string contestId, [FromServices] ContestExecutorFactory cef, CancellationToken token)
        {
            var ce = cef.Create(contestId);
            if (!ce.HasPermissionToContest())
            {
                return Result(401, "No permission");
            }

            var user = await User.Manager.FindByNameAsync(username);

            if (!await DB.UserClaims.AnyAsync(x => x.ClaimValue == contestId && x.ClaimType == Constants.ContestEditPermission && x.UserId == user.Id, token))
            {
                return Result(404, "Claim not found");
            }
            else if (username == User.Current.UserName)
            {
                return Result(400, "Cannot remove yourself");
            }
            else
            {
                await DB.UserClaims
                    .Where(x => x.ClaimValue == contestId && x.ClaimType == Constants.ContestEditPermission && x.UserId == user.Id)
                    .DeleteAsync(token);

                return Result(200, "Delete succeeded");
            }
        }
        #endregion

        #region Standings
        [HttpGet("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/standings/all")]
        public async Task<IActionResult> GetStandings(string contestId, bool? includingVirtual, [FromServices] ContestExecutorFactory cef, CancellationToken token)
        {
            var ce = cef.Create(contestId);
            if (!ce.IsAvailableToGetStandings(User.Current?.UserName) && !ce.HasPermissionToContest())
            {
                return Result(401, "No permission");
            }

            var contest = await DB.Contests.SingleOrDefaultAsync(x => x.Id == contestId, token);
            if (contest == null)
            {
                return Result(404, "Not found");
            }

            if (!includingVirtual.HasValue)
                includingVirtual = true;

            var problems = DB.ContestProblems
                .Where(x => x.ContestId == contestId)
                .OrderBy(x => x.Number)
                .Select(x => new ContestExecutor.ProblemSummary
                {
                    number = x.Number,
                    point = x.Point,
                    id = x.ProblemId
                })
                .ToList();

            var attendees = await ce.GenerateFullStandingsAsync(includingVirtual.Value, token);

            return Result(new Standings {
                id = contestId,
                title = contest.Title,
                columnDefinations = ce.PointColumnDefinations,
                problems = problems,
                attendees = attendees
            });
        }

        [HttpGet("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/standings/{userId:Guid}")]
        public async Task<IActionResult> GetStandings(string contestId, Guid userId, [FromServices] ContestExecutorFactory cef, CancellationToken token)
        {
            var ce = cef.Create(contestId);
            if (!ce.IsAvailableToGetStandings(User.Current?.UserName) && !ce.HasPermissionToContest())
            {
                return Result(401, "No permission");
            }

            var username = (await DB.Users.SingleOrDefaultAsync(x => x.Id == userId)).UserName;
            var ret = await ce.GenerateSingleStandingsAsync(username, token);
            if (ret == null)
            {
                return Result(404, "Standings of this user is not found");
            }
            return Result(ret);
        }
        #endregion

        #region Lock

        [HttpPut("{contestId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/problem/{problemId:regex(^[[a-zA-Z0-9-_]]{{4,128}}$)}/lock")]
        public async Task<IActionResult> PutLock(
            string contestId, 
            string problemId, 
            [FromServices] ContestExecutorFactory cef,
            CancellationToken token)
        {
            if (User.Current == null)
            {
                return Result(401, "Not authorized");
            }
            else
            {
                var ce = cef.Create(contestId);

                if (!ce.AllowLockProblem)
                {
                    return Result(400, "This contest does not accept lock problem.");
                }

                var status = await DB.ContestProblemLastStatuses
                    .Where(x => x.ContestId == contestId)
                    .Where(x => x.UserId == User.Current.Id)
                    .Where(x => x.ProblemId == problemId)
                    .SingleOrDefaultAsync(token);

                if (status == null)
                {
                    return Result(404, "Please pass the small data of this problem first.");
                }
                else if (status.IsLocked)
                {
                    return Result(400, "Already locked");
                }
                else if (!status.IsHackable)
                {
                    return Result(400, "Please pass the small data of this problem first.");
                }
                else
                {
                    status.IsLocked = true;
                    await DB.SaveChangesAsync(token);
                    return Result(200, "Put succeeded");
                }
            }
        }
        #endregion

        #region Private Functions
        private const string ProblemNumberString = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

        private async Task<bool> HasPermissionToProblemAsync(string problemId, CancellationToken token = default(CancellationToken))
            => !(User.Current == null
               || !await User.Manager.IsInAnyRolesAsync(User.Current, Constants.MasterOrHigherRoles)
               && !await DB.UserClaims.AnyAsync(x => x.UserId == User.Current.Id
                   && x.ClaimType == Constants.ProblemEditPermission
                   && x.ClaimValue == problemId));

        private async Task<bool> IsRegisteredToContest(string contestId, CancellationToken token)
            => User.IsSignedIn() && await DB.Attendees.AnyAsync(x => x.ContestId == contestId && x.UserId == User.Current.Id);
        #endregion
    }
}
