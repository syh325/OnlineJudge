﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using JoyOI.OnlineJudge.ContestExecutor;
using JoyOI.OnlineJudge.Models;
using JoyOI.OnlineJudge.WebApi.Models;
using JoyOI.OnlineJudge.WebApi.Hubs;
using JoyOI.ManagementService.SDK;
using Newtonsoft.Json;

namespace JoyOI.OnlineJudge.WebApi.Controllers.Api
{
    [Route("api/[controller]")]
    public class HackController : BaseController
    {
        [HttpGet("all")]
        public async Task<IActionResult> Get(
            HackResult? hackStatus,
            JudgeResult? judgeStatus,
            string problemId, 
            string hacker,
            string hackee,
            DateTime? begin, 
            DateTime? end, 
            string contestId, 
            int? page,
            [FromServices] ContestExecutorFactory cef,
            CancellationToken token)
        {
            IQueryable<HackStatus> ret = DB.HackStatuses
                .Include(x =>x.Status);

            if (!page.HasValue)
            {
                page = 1;
            }

            if (hackStatus.HasValue)
            {
                ret = ret.Where(x => x.Result == hackStatus.Value);
            }

            if (judgeStatus.HasValue)
            {
                ret = ret.Where(x => x.HackeeResult == judgeStatus.Value);
            }

            if (!string.IsNullOrEmpty(hacker))
            {
                ret = ret.Where(x => x.User.UserName == hacker);
            }
            
            if (!string.IsNullOrEmpty(hackee))
            {
                ret = ret.Where(x => x.Status.User.UserName == hackee);
            }

            if (begin.HasValue)
            {
                ret = ret.Where(x => x.Time >= begin.Value);
            }

            if (end.HasValue)
            {
                ret = ret.Where(x => x.Time <= end.Value);
            }

            if (!string.IsNullOrEmpty(contestId))
            {
                ret = ret.Where(x => x.ContestId == contestId);
            }

            if (!string.IsNullOrEmpty(problemId))
            {
                ret = ret.Where(x => x.Status.ProblemId == problemId);
            }

            if (IsGroupRequest())
            {
                ret = ret.Where(x => x.GroupId == CurrentGroup.Id);
            }

            return await Paged(ret.OrderByDescending(x => x.Time)
                .Select(x => new HackViewModel
                {
                    Id = x.Id,
                    problemId = x.Status.ProblemId,
                    HackerId = x.UserId,
                    HackeeId = x.Status.UserId,
                    HackResult = x.Result,
                    JudgeResult = x.HackeeResult,
                    Time = x.Time,
                    TimeUsedInMs = x.TimeUsedInMs,
                    MemoryUsedInByte = x.MemoryUsedInByte,
                    JudgeStatusId = x.JudgeStatusId
                }), 
                page.Value, 
                20, 
                token);
        }

        [HttpGet("{id:Guid}")]
        public async Task<IActionResult> Get(
            Guid id,
            [FromServices] ManagementServiceClient mgmt,
            [FromServices] ContestExecutorFactory cef,
            CancellationToken token)
        {
            var ret = await DB.HackStatuses
                .Include(x => x.Status)
                .Include(x => x.User)
                .SingleOrDefaultAsync(x => x.Id == id, token);

            if (IsGroupRequest() && ret.GroupId != CurrentGroup.Id)
            {
                return Result(400, "No permission");
            }

            if (ret.HackDataBlobId.HasValue)
            {
                ret.HackDataBody = Encoding.UTF8.GetString((await mgmt.GetBlobAsync(ret.HackDataBlobId.Value, token)).Body);
            }

            var username = ret.User.UserName;
            ret.User = null;

            if (ret == null)
            {
                return Result(404, "Hack status is not found");
            }

            if (!string.IsNullOrEmpty(ret.ContestId))
            {
                var ce = cef.Create(ret.ContestId);
                if ((ce.IsContestInProgress(User.Current?.UserName) || ce.IsContestInProgress(username)) && ret.UserId != User.Current?.Id && !IsMasterOrHigher && !ce.HasPermissionToContest(User.Current?.UserName))
                {
                    ce.OnShowHackResult(ret);
                }
            }

            if (User.Current?.Id != ret.UserId 
                && User.Current?.Id != ret.Status.UserId 
                && !await HasPermissionToProblemAsync(ret.Status.ProblemId, token)
                && !(IsGroupRequest() && await HasPermissionToGroupAsync(token)))
            {
                ret.HackDataBody = null;
            }

            return Result(ret);
        }

        [HttpPut]
        public async Task<IActionResult> Put(
            [FromServices] ContestExecutorFactory cef, 
            [FromServices] ManagementServiceClient mgmt,
            [FromServices] IHubContext<OnlineJudgeHub> hub,
            CancellationToken token)
        {
            var request = JsonConvert.DeserializeObject<HackRequest>(RequestBody);

            if (!User.IsSignedIn())
            {
                return Result(403, "Please login first.");
            }

            var judge = await DB.JudgeStatuses
                .Include(x => x.Problem)
                .SingleOrDefaultAsync(x => x.Id == request.JudgeStatusId, token);

            if (judge == null)
            {
                return Result(404, "The judge status is not found");
            }

            if (!judge.BinaryBlobId.HasValue)
            {
                return Result(400, "The lagency status could not be hacked.");
            }

            if (judge.UserId == User.Current.Id)
            {
                return Result(400, "You cannot hack yourself.");
            }
            
            if (IsGroupRequest() && !await IsGroupMemberAsync(token))
            {
                return Result(401, "You are not a member of this group.");
            }

            if (!string.IsNullOrEmpty(request.ContestId))
            {
                var ce = cef.Create(request.ContestId);

                if (!ce.IsStatusHackable(judge) || !ce.IsContestInProgress())
                {
                    return Result(400, "You cannot hack this status");
                }

                if (await DB.HackStatuses.AnyAsync(x => x.UserId == User.Current.Id && x.JudgeStatusId == request.JudgeStatusId && x.Result == HackResult.Succeeded && x.ContestId == request.ContestId, token))
                {
                    return Result(400, "You have already hacked this status.");
                }
            }

            // Upload hack data to management service
            if (request.IsBase64)
            {
                request.Data = request.Data.Substring(request.Data.IndexOf("base64,") + "base64,".Length);
            }
            var blobId = await mgmt.PutBlobAsync("data.txt", request.IsBase64 ? Convert.FromBase64String(request.Data) : Encoding.UTF8.GetBytes(request.Data), token);

            var hack = new HackStatus
            {
                ContestId = request.ContestId,
                JudgeStatusId = judge.Id,
                HackDataBlobId = blobId,
                Result = HackResult.Pending,
                Time = DateTime.UtcNow,
                UserId = User.Current.Id,
                HackeeResult = JudgeResult.Pending
            };
            DB.HackStatuses.Add(hack);
            await DB.SaveChangesAsync(token);

            var blobs = new List<BlobInfo>(10);

            // Put the limit.json into blob collection
            blobs.Add(new BlobInfo
            {
                Id = await mgmt.PutBlobAsync("limit.json", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    UserTime = judge.Problem.TimeLimitationPerCaseInMs,
                    PhysicalTime = judge.Problem.TimeLimitationPerCaseInMs * 4,
                    Memory = judge.Problem.MemoryLimitationPerCaseInByte
                }))),
                Name = "limit.json",
                Tag = "Problem=" + judge.ProblemId
            });

            // Put the data into blob collection
            blobs.Add(new BlobInfo(blobId, "data.txt"));

            // Put the hackee program into blob collection
            blobs.Add(new BlobInfo(judge.BinaryBlobId.Value, "Hackee" + Constants.GetBinaryExtension(judge.Language), judge.Id.ToString()));

            // Put range validator program into blob collection
            if (judge.Problem.RangeBlobId.HasValue && string.IsNullOrEmpty(judge.Problem.RangeError))
            {
                blobs.Add(new BlobInfo(judge.Problem.RangeBlobId.Value, "Range" + Constants.GetBinaryExtension(judge.Problem.RangeLanguage)));
            }

            // Put standard program into blob collection
            if (judge.Problem.StandardBlobId.HasValue && string.IsNullOrEmpty(judge.Problem.StandardError))
            {
                blobs.Add(new BlobInfo(judge.Problem.StandardBlobId.Value, "Standard" + Constants.GetBinaryExtension(judge.Problem.StandardLanguage)));
            }
            else
            {
                // Get 3 accepted programs instead of standard program
                var statuses = await DB.JudgeStatuses
                    .Where(x => x.Result == JudgeResult.Accepted && x.ProblemId == judge.ProblemId && x.BinaryBlobId.HasValue && x.Id != judge.Id)
                    .OrderBy(x => x.TimeUsedInMs)
                    .ThenBy(x => x.MemoryUsedInByte)
                    .Take(3)
                    .ToListAsync(token);

                if (statuses.Count == 0)
                {
                    return Result(400, "Missing standard program, this status could not be hacked");
                }

                var standards = statuses.Select(x => new BlobInfo(x.BinaryBlobId.Value, "Standard-" + x.Id.ToString().Substring(0,8) + Constants.GetBinaryExtension(x.Language), x.Id.ToString()));
                blobs.AddRange(standards);
            }

            // Put validator into blob collection
            if (judge.Problem.ValidatorBlobId.HasValue)
            {
                blobs.Add(new BlobInfo(judge.Problem.ValidatorBlobId.Value, "Validator" + Constants.GetBinaryExtension(judge.Problem.ValidatorLanguage)));
            }
            else
            {
                blobs.Add(new BlobInfo(Guid.Parse(Configuration["JoyOI:StandardValidatorBlobId"]), "Validator.out"));
            }

            // Start hack state machine
            var stateMachineId = await mgmt.PutStateMachineInstanceAsync("HackStateMachine", Configuration["ManagementService:CallBack"], blobs.ToArray(), 1, token);
            var stateMachine = new StateMachine { CreatedTime = DateTime.Now, Name = "HackStateMachine", Id = stateMachineId };
            DB.StateMachines.Add(stateMachine);
            hack.RelatedStateMachineIds = new List<HackStatusStateMachine>();
            hack.RelatedStateMachineIds.Add(new HackStatusStateMachine { StateMachineId = stateMachine.Id, StatusId = judge.Id });
            await DB.SaveChangesAsync(token);

            hub.Clients.All.InvokeAsync("ItemUpdated", "hack", hack.Id);

            return Result(hack.Id);
        }

        [HttpGet("{id:Guid}/data")]
        public async Task<IActionResult> GetData(
            Guid id,
            [FromServices] ManagementServiceClient mgmt,
            [FromServices] ContestExecutorFactory cef,
            CancellationToken token)
        {
            var hack = await DB.HackStatuses
                .Include(x => x.Status)
                .SingleOrDefaultAsync(x => x.Id == id);

            if (hack == null)
            {
                return Result(404, "Hack status is not found");
            }

            if (!hack.HackDataBlobId.HasValue)
            {
                return Result(404, "The hack data is not found");
            }

            if (!string.IsNullOrEmpty(hack.ContestId))
            {
                var ce = cef.Create(hack.ContestId);
                if (!IsMasterOrHigher && !await HasPermissionToProblemAsync(hack.Status.ProblemId, token) && ce.IsContestInProgress() && User.Current?.Id != hack.UserId)
                {
                    return Result(401, "No permission");
                }
            }
            else
            {
                if (!IsMasterOrHigher && !await HasPermissionToProblemAsync(hack.Status.ProblemId, token) && User.Current?.Id != hack.UserId)
                {
                    return Result(401, "No permission");
                }
            }

            var blob = await mgmt.GetBlobAsync(hack.HackDataBlobId.Value, token);
            return Result(Encoding.UTF8.GetString(blob.Body));
        }

        #region Private Functions
        private async Task<bool> HasPermissionToProblemAsync(string problemId, CancellationToken token = default(CancellationToken))
            => User.Current != null && (await User.Manager.IsInAnyRolesAsync(User.Current, Constants.MasterOrHigherRoles)
            || await DB.UserClaims.AnyAsync(x => x.UserId == User.Current.Id
                   && x.ClaimType == Constants.ProblemEditPermission
                   && x.ClaimValue == problemId));
        #endregion
    }
}
