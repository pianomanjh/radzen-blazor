using System;
using System.Collections.Generic;

namespace Radzen.Blazor.Benchmarks;

public enum Status { Active, Inactive, Pending, Archived }

public sealed class Address
{
    public string Street { get; set; }
    public string City { get; set; }
    public string Country { get; set; }
}

public sealed class Person
{
    public int Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Email { get; set; }
    public DateTime HireDate { get; set; }
    public decimal Salary { get; set; }
    public bool IsManager { get; set; }
    public Status Status { get; set; }
    public Address Address { get; set; }

    public static List<Person> Generate(int count)
    {
        // Deterministic pseudo-random data (no Random -> stable benchmarks).
        var cities = new[] { "London", "Paris", "Berlin", "Madrid", "Rome", "Vienna" };
        var countries = new[] { "UK", "France", "Germany", "Spain", "Italy", "Austria" };
        var list = new List<Person>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new Person
            {
                Id = i,
                FirstName = "First" + i,
                LastName = "Last" + i,
                Email = "user" + i + "@example.com",
                HireDate = new DateTime(2000, 1, 1).AddDays(i % 8000),
                Salary = 40000m + (i % 100) * 137m,
                IsManager = (i % 7) == 0,
                Status = (Status)(i % 4),
                Address = new Address
                {
                    Street = i + " Main St",
                    City = cities[i % cities.Length],
                    Country = countries[i % countries.Length],
                }
            });
        }
        return list;
    }
}
