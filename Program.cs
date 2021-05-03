using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using RestSharp;
using Spectre.Console;

namespace CanvasSucks
{
    class Program
    {
        static RestClient client;
        static void Main(string[] args)
        {
            client = new RestClient("https://nebo.instructure.com/api/v1");
            var observer = GetCurrentUser();
            ListAssignmentsForStudent(observer.Id, Settings.StudentName);
        }

        private static void ListAssignmentsForStudent(int observerId, string studentName)
        {
            var user = GetObserveeByName(observerId, studentName);
            System.Console.WriteLine($"{user.Name} - {user.Id}");
            var courses = GetCoursesForUser(user.Id);
            foreach (var course in courses)
            {
                AnsiConsole.Render(new Rule(course.Name));
                var assignments = GetAssignmentsForUserAndCourse(user.Id, course.Id);
                assignments = FilterToRelevantAssignments(assignments);
                var table = new Table();
                table.AddColumns("Name", "Submitted", "Due At", "Lock At", "Score", "State", "Graded by");
                foreach (var assignment in assignments.OrderBy(x => x.LockAt))
                {
                    var submitted = assignment.HasSubmittedSubmissions ? new Markup("yes") : new Markup("[red]no[/]");
                    var scoreText = $"{assignment.Score}/{assignment.PointsPossible}";
                    var score = assignment.Score < assignment.PointsPossible ? new Markup($"[red]{scoreText}[/]") : new Markup(scoreText);
                    table.AddRow(
                        new Markup(assignment.Name),
                        submitted,
                        new Markup(assignment.DueAt.ToString()),
                        new Markup(assignment.LockAt.ToString()),
                        score,
                        new Markup(assignment.WorkflowState),
                        new Markup(assignment.GradedBy));
                }
                AnsiConsole.Render(table);
            }
        }

        private static List<Assignment> FilterToRelevantAssignments(List<Assignment> assignments)
        {
            return assignments
                .Where(x => x.LockAt > DateTime.UtcNow)
                .Where(x => x.UnlockAt < DateTime.UtcNow)
                .Where(x => x.PointsPossible > 0)
                .Where(x => (x.WorkflowState == "graded" && x.Score < x.PointsPossible) || x.WorkflowState != "graded")
                .ToList();
        }

        static List<Assignment> GetAssignmentsForUserAndCourse(int userId, int courseId)
        {
            var assignments = new List<Assignment>();
            var assignmentUrl = $"users/{userId}/courses/{courseId}/assignments";
            while (assignmentUrl != null)
            {
                var assignmentRequest = new RestRequest(assignmentUrl, Method.GET).AddAuthorization();
                var assignmentResponse = client.Execute<List<Assignment>>(assignmentRequest);
                assignments.AddRange(assignmentResponse.Data);
                assignmentUrl = GetNextLink(assignmentResponse);
            }
            var submissionsUrl = $"courses/{courseId}/students/submissions?student_ids[]={userId}";
            while (submissionsUrl != null)
            {
                var submissionsRequest = new RestRequest(submissionsUrl, Method.GET).AddAuthorization();
                var submissionsResponse = client.Execute<List<AssignmentSubmission>>(submissionsRequest);
                submissionsUrl = GetNextLink(submissionsResponse);
                foreach (var assignment in assignments)
                {
                    var submissions = submissionsResponse.Data.Where(x => x.AssignmentId == assignment.Id).OrderBy(x => x.Attempt);
                    if (submissions.Count() <= 0)
                    {
                        continue;
                    }
                    var lastSubmission = submissions.Last();
                    assignment.Score = lastSubmission.Score;
                    assignment.GraderId = lastSubmission.GraderId;
                    assignment.WorkflowState = lastSubmission.WorkflowState;
                }
            }
            return assignments;
        }

        static string GetNextLink(IRestResponse response)
        {
            var linkHeaderValue = response.Headers.SingleOrDefault(x => x.Name == "Link")?.Value as string;
            return linkHeaderValue.Split(',').FirstOrDefault(x => x.Contains("rel=\"next\""))?.Split(';').First().Trim('<', '>');
        }

        private static User GetCurrentUser()
        {
            var request = new RestRequest($"users/self", Method.GET).AddAuthorization();
            var response = client.Execute<User>(request);
            return response.Data;
        }

        static User GetObserveeByName(int userId, string name)
        {
            return GetObservees(userId).Single(x => x.Name == name);
        }

        static List<User> GetObservees(int userId)
        {
            var request = new RestRequest($"users/{userId}/observees", Method.GET).AddAuthorization();
            var response = client.Execute<List<User>>(request);
            return response.Data;
        }

        static List<Course> GetCoursesForUser(int userId)
        {
            var request = new RestRequest($"users/{userId}/courses", Method.GET).AddAuthorization();
            var response = client.Execute<List<Course>>(request);
            return response.Data;
        }
    }

    public static class RequestExtensions
    {
        public static RestRequest AddAuthorization(this RestRequest request)
        {
            request.AddHeader("Authorization", "Bearer " + Settings.AccessToken);
            return request;
        }
    }
}

public static class Settings
{
    public static readonly string AccessToken;
    public static readonly string StudentName;

    static Settings()
    {
        var config = new ConfigurationBuilder()
                        .AddUserSecrets<CanvasSucks.Program>()
                        .Build();
        AccessToken = config.GetValue<string>("AccessToken");
        if (AccessToken == null) throw new ArgumentException("Expected AccessToken to be in user secrets\r\nAdd with the following command:\r\ndotnet user-secrets set AccessToken <access-token-value>");
        StudentName = config.GetValue<string>("StudentName");
        if (StudentName == null) throw new ArgumentException("Expected StudentName to be in user secrets\r\nAdd with the following command:\r\ndotnet user-secrets set StudentName \"FirstName LastName\"");
    }
}

public class AssignmentSubmission
{
    public int AssignmentId { get; set; }
    public int GraderId { get; set; }
    public int Score { get; set; }
    public int Attempt { get; set; }
    public string WorkflowState { get; set; }

    public override string ToString()
    {
        return $"AssignmentId:{AssignmentId} Score:{Score} WorkflowState:{WorkflowState}";
    }
}

public class Assignment
{
    public int Id { get; set; }
    public string Name { get; set; }
    public bool HasSubmittedSubmissions { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime LockAt { get; set; }
    public DateTime UnlockAt { get; set; }
    public int GraderId { get; set; }
    public int Score { get; set; }
    public int PointsPossible { get; set; }
    public string WorkflowState { get; set; }
    public string GradedBy => GraderId == 0 ? "not graded" : GraderId > 0 ? "teacher" : "automatic";


    public override string ToString() => $"Assignment Id:{Id} Name:{Name} HasSubmittedSubmissions:{HasSubmittedSubmissions} DueAt:{DueAt} LockAt:{LockAt}";
}

public class User
{
    public int Id { get; set; }
    public string Name { get; set; }

    public override string ToString() => $"User Id:{Id} Name:{Name}";
}

public class Course
{
    public int Id { get; set; }
    public string Name { get; set; }

    public override string ToString()
    {
        return $"Course Id:{Id} Name:{Name}";
    }
}
