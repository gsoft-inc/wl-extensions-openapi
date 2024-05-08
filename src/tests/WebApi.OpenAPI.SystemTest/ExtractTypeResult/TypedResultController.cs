﻿using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Swashbuckle.AspNetCore.Annotations;
using Workleap.Extensions.OpenAPI.TypedResult;

namespace WebApi.OpenAPI.SystemTest.ExtractTypeResult;

[ApiController]
[Route("typedResult")]
[Produces("application/json")]
[Consumes("application/json")]
public class TypedResultController : ControllerBase
{
    [HttpGet]
    [Route("/withOnlyOnePath")]
    public Ok<TypedResultExample> TypeResultWithOnlyOnePath(int id)
    {
        return TypedResults.Ok(new TypedResultExample("Example"));
    }

    [HttpGet]
    [Route("/withAnnotation")]
    [ProducesResponseType(typeof(TypedResultExample), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Results<Ok<ProblemDetails>, BadRequest<ProblemDetails>, NotFound> TypedResultWithAnnotation(int id)
    {
        return id switch
        {
            < 0 => TypedResults.NotFound(),
            0 => TypedResults.BadRequest(new ProblemDetails()),
            _ => TypedResults.Ok(new ProblemDetails())
        };
    }

    [HttpGet]
    [Route("/withNoAnnotation")]
    public Results<Ok<TypedResultExample>, BadRequest<ProblemDetails>, NotFound> TypedResultWithNoAnnotation(int id)
    {
        return id switch
        {
            < 0 => TypedResults.NotFound(),
            0 => TypedResults.BadRequest(new ProblemDetails()),
            _ => TypedResults.Ok(new TypedResultExample("Example"))
        };
    }

    [HttpGet]
    [Route("/withNoAnnotationForAcceptedAndUnprocessableResponse")]
    public Results<Ok<TypedResultExample>, Accepted<TypedResultExample>, UnprocessableEntity> TypedResultWithNoAnnotationForAcceptedAndUnprocessableResponse(int id)
    {
        return id switch
        {
            < 0 => TypedResults.UnprocessableEntity(),
            0 => TypedResults.Ok(new TypedResultExample("Example")),
            _ => TypedResults.Accepted("hardcoded uri", new TypedResultExample("Example"))
        };
    }

    [HttpGet]
    [Route("/voidOk")]
    public Ok VoidOk(int id)
    {
        return TypedResults.Ok();
    }

    [HttpGet]
    [Route("/withSwaggerResponseAnnotation")]
    [SwaggerResponse(StatusCodes.Status200OK, "Returns TypedResult", typeof(TypedResultExample), "application/json")]
    public Ok<ProblemDetails> TypedResultWithSwaggerResponseAnnotation()
    {
        return TypedResults.Ok(new ProblemDetails());
    }

    [HttpGet]
    [Route("/withExceptionsWithExtensions")]
    public Results<Ok<TypedResultExample>, Forbidden<string>, InternalServerError<string>> TypedResultWithExceptionsWithExtensions(int id)
    {
        return id switch
        {
            0 => TypedResultsExtensions.Forbidden("Forbidden"),
            < 0 => TypedResultsExtensions.InternalServerError("An error occured when processing the request."),
            _ => TypedResults.Ok(new TypedResultExample("Example"))
        };
    }
}