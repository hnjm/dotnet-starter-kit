﻿using FluentValidation;
using MediatR;

namespace FSH.WebApi.Catalog.Application.Products.Creation.v1;
public sealed record ProductCreationCommand(string? Name, decimal Price, string? Description = null) : IRequest<Guid>;

public class ProductCreationValidator : AbstractValidator<ProductCreationCommand>
{
    public ProductCreationValidator()
    {
        RuleFor(p => p.Name).NotEmpty().MinimumLength(10).MaximumLength(75);
        RuleFor(p => p.Price).GreaterThan(0);
    }
}